using System.Text;
using System.Text.Json;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Server;

namespace SignFonts;

public sealed partial class SignFontsModSystem
{
    private const int ConfirmedSavePacketId = 48725;
    private const int SaveAcknowledgementPacketId = 48726;
    private const string SignBackupKey = "signfonts:sign-backup-v1";
    private static ICoreServerAPI persistentServerApi;
    private static readonly object signBackupLock = new();
    private static Dictionary<string, StoredSign> signBackup = new();
    private static readonly HashSet<string> pendingSignSaves = new();

    private sealed class StoredSign
    {
        public string BlockCode { get; set; }
        public string[] Texts { get; set; }
        public string Family { get; set; }
        public string Formatting { get; set; }
        public float? BaseSize { get; set; }
    }

    private sealed class ConfirmedSignSave
    {
        public string RequestId { get; set; }
        public string[] Texts { get; set; }
        public string Family { get; set; }
        public string Formatting { get; set; }
        public float? BaseSize { get; set; }
    }

    private sealed class SignSaveReply
    {
        public string RequestId { get; set; }
        public bool Success { get; set; }
        public string Message { get; set; }
    }

    private void StartPersistentSignStorage(ICoreServerAPI api)
    {
        persistentServerApi = api;
        lock (signBackupLock) signBackup = new();
        api.Event.SaveGameLoaded += LoadSignBackup;
        api.Event.GameWorldSave += StoreSignBackup;
        if (api.WorldManager.SaveGame != null) LoadSignBackup();
    }

    private static void LoadSignBackup()
    {
        var bytes = persistentServerApi?.WorldManager.SaveGame?.GetData(SignBackupKey);
        if (bytes == null || bytes.Length == 0) return;
        try
        {
            var loaded = JsonSerializer.Deserialize<Dictionary<string, StoredSign>>(bytes) ?? new();
            var valid = new Dictionary<string, StoredSign>();
            foreach (var entry in loaded)
            {
                var value = entry.Value;
                if (value?.BlockCode == null || value.Texts == null || value.Texts.Length is < 1 or > 8) continue;
                try
                {
                    ValidateStoredStyle(value.Family, value.Formatting, value.Texts, value.BaseSize);
                    valid[entry.Key] = value;
                }
                catch (Exception error) when (error is ArgumentException || error is JsonException)
                {
                    persistentServerApi.Logger.Warning("SignFonts: skipped an invalid formatting backup entry at {0}.", entry.Key);
                }
            }
            lock (signBackupLock) signBackup = valid;
            persistentServerApi.Logger.Notification("SignFonts: loaded {0} sign formatting backup records from this world.", valid.Count);
        }
        catch (JsonException error)
        {
            persistentServerApi.Logger.Error("SignFonts: could not load the world formatting backup: {0}", error.Message);
        }
    }

    private static void StoreSignBackup()
    {
        var save = persistentServerApi?.WorldManager.SaveGame;
        if (save == null) return;
        byte[] bytes;
        lock (signBackupLock) bytes = JsonSerializer.SerializeToUtf8Bytes(signBackup);
        save.StoreData(SignBackupKey, bytes);
    }

    private void StopPersistentSignStorage()
    {
        if (isClientInstance || persistentServerApi == null) return;
        StoreSignBackup();
        persistentServerApi.Event.SaveGameLoaded -= LoadSignBackup;
        persistentServerApi.Event.GameWorldSave -= StoreSignBackup;
        persistentServerApi = null;
    }

    private static string ServerSignKey(BlockEntity entity) => $"{entity.Pos.dimension}:{entity.Pos.X},{entity.Pos.Y},{entity.Pos.Z}";

    private static void RememberServerSign(BlockEntity entity)
    {
        if (entity.Api?.Side != EnumAppSide.Server || persistentServerApi == null) return;
        var state = fontStates.GetOrCreateValue(entity);
        var size = FindField(entity.GetType(), "fontSize")?.GetValue(entity) as float?;
        var stored = new StoredSign
        {
            BlockCode = entity.Block?.Code?.ToString(), Texts = EntitySignText(entity),
            Family = state.SelectedFamily, Formatting = state.Formatting?.Serialize() ?? "", BaseSize = size
        };
        lock (signBackupLock) signBackup[ServerSignKey(entity)] = stored;
        // This updates the world's mod data immediately; the normal world-save
        // cycle commits it to disk alongside the rest of the savegame.
        StoreSignBackup();
    }

    private static void AfterServerSignInitialize(object __instance)
    {
        if (__instance is not BlockEntity entity || entity.Api?.Side != EnumAppSide.Server) return;
        var state = fontStates.GetOrCreateValue(entity);
        state.ServerManaged = true;
        StoredSign stored;
        lock (signBackupLock) signBackup.TryGetValue(ServerSignKey(entity), out stored);
        if (stored != null && stored.BlockCode == entity.Block?.Code?.ToString() && stored.Texts.SequenceEqual(EntitySignText(entity)))
        {
            state.SelectedFamily = stored.Family;
            state.Formatting = SignFormatting.Parse(stored.Formatting);
            // Vanilla owns the base-size field. Restore only our font and rich
            // styles, so another mod's legitimate base-size edit is preserved.
            // Chunk data is refreshed so reconnecting clients receive the restored state.
            entity.MarkDirty(true);
        }
        else if (stored != null)
        {
            // Do not transfer styling onto a different block or changed wording.
            lock (signBackupLock) signBackup.Remove(ServerSignKey(entity));
        }
        else if (state.SelectedFamily != null || state.Formatting?.HasFormatting == true)
        {
            RememberServerSign(entity);
        }
    }

    private static void BeforeStoredSignRemoved(object __instance)
    {
        if (__instance is not BlockEntity entity || entity.Api?.Side != EnumAppSide.Server || entity.Pos == null) return;
        if (entity.GetType().Name != "BlockEntitySign" && entity.GetType().Name != "BlockEntitySignPost") return;
        lock (signBackupLock) signBackup.Remove(ServerSignKey(entity));
    }

    private static void SendConfirmedSignSave(SignEditSession session, object dialog)
    {
        var request = new ConfirmedSignSave
        {
            RequestId = Guid.NewGuid().ToString("N"), Texts = session.DraftFormatting.Fields.Select(f => f.Text).ToArray(),
            Family = session.DraftFamily, Formatting = session.DraftFormatting.Serialize(),
            BaseSize = FindField(dialog.GetType(), "FontSize")?.GetValue(dialog) as float?
        };
        var bytes = JsonSerializer.SerializeToUtf8Bytes(request);
        if (bytes.Length > 262144)
        {
            clientApi.ShowChatMessage("Sign Fonts: formatting is too large to save on the server. Reduce the text or formatting.");
            return;
        }
        pendingSignSaves.Add(request.RequestId);
        clientApi.Network.SendBlockEntityPacket(session.Entity.Pos, ConfirmedSavePacketId, bytes);
        clientApi.Event.RegisterCallback(_ =>
        {
            if (pendingSignSaves.Remove(request.RequestId))
                clientApi.ShowChatMessage("Sign Fonts: the server did not confirm this formatting save. Check that server and client both use 1.3.2.");
        }, 10000);
    }

    private static SignFormatting ValidateStoredStyle(string family, string formatting, string[] texts, float? baseSize)
    {
        if (family?.Length > 128 || family?.Any(char.IsControl) == true) throw new ArgumentException("Invalid font family.");
        if (texts == null || texts.Length is < 1 or > 8 || texts.Any(t => t == null || t.Length > 8192) || texts.Sum(t => t.Length) > 16384)
            throw new ArgumentException("Sign text exceeds the formatting limit.");
        if (baseSize.HasValue && (!float.IsFinite(baseSize.Value) || baseSize.Value < 1 || baseSize.Value > 200))
            throw new ArgumentException("Invalid sign base size.");
        var doc = SignFormatting.Parse(formatting);
        if (doc != null && (doc.Fields.Count != texts.Length || doc.Fields.Where((f, i) => f.Text != texts[i]).Any()))
            throw new ArgumentException("Formatting text does not match sign wording.");
        return doc;
    }

    private static bool ReceiveConfirmedSignSave(object instance, IPlayer player, byte[] data)
    {
        if (instance is not BlockEntity entity || entity.Api?.Side != EnumAppSide.Server || data == null || data.Length > 262144) return false;
        ConfirmedSignSave request = null;
        try
        {
            request = JsonSerializer.Deserialize<ConfirmedSignSave>(data) ?? throw new ArgumentException("Missing save request.");
            if (request.RequestId == null || !Guid.TryParseExact(request.RequestId, "N", out _)) throw new ArgumentException("Invalid save request ID.");
#pragma warning disable CS0618
            var permissions = new BlockEntity.CachedAccessPerms(entity.Api.World, entity.Pos, player);
#pragma warning restore CS0618
            if (!permissions.IsInteractingPlayerAllowedTo(EnumBlockAccessFlags.Use, true, "signfonts"))
                throw new ArgumentException("You cannot edit this sign here.");
            var doc = ValidateStoredStyle(request.Family, request.Formatting, request.Texts, request.BaseSize);
            if (!request.Texts.SequenceEqual(EntitySignText(entity)))
                throw new ArgumentException("The server did not accept the same sign wording. Reopen the sign and try saving again.");
            // Vanilla already authorised and saved the base-size/text packet. Verify
            // it rather than overriding the result of another mod's permission check.
            if (request.BaseSize.HasValue && FindField(entity.GetType(), "fontSize")?.GetValue(entity) is float actualSize && actualSize != request.BaseSize.Value)
                throw new ArgumentException("The server did not accept the sign's base size.");
            var state = fontStates.GetOrCreateValue(entity);
            state.SelectedFamily = request.Family;
            state.Formatting = doc;
            state.ServerManaged = true;
            RememberServerSign(entity);
            entity.MarkDirty(true);
            entity.Api.World.BlockAccessor.GetChunkAtBlockPos(entity.Pos)?.MarkModified();
            entity.Api.Logger.Notification("SignFonts: accepted formatting save at {0}; stored on the sign and in this world's backup.", entity.Pos);
            ReplyToSignSave(entity, player, request.RequestId, true, "Sign formatting saved on server.");
        }
        catch (Exception error) when (error is ArgumentException || error is JsonException)
        {
            entity.Api.Logger.Warning("SignFonts: rejected formatting save at {0}: {1}", entity.Pos, error.Message);
            if (request?.RequestId is { Length: <= 32 }) ReplyToSignSave(entity, player, request.RequestId, false, error.Message);
            entity.MarkDirty(true);
        }
        return false;
    }

    private static void ReplyToSignSave(BlockEntity entity, IPlayer player, string requestId, bool success, string message)
    {
        if (player is not IServerPlayer serverPlayer || entity.Api is not ICoreServerAPI api) return;
        api.Network.SendBlockEntityPacket(serverPlayer, entity.Pos, SaveAcknowledgementPacketId,
            JsonSerializer.SerializeToUtf8Bytes(new SignSaveReply { RequestId = requestId, Success = success, Message = message }));
    }

    private static bool BeforeSignSaveAcknowledgement(int packetid, byte[] data)
    {
        if (packetid != SaveAcknowledgementPacketId) return true;
        if (data == null || data.Length > 4096) return false;
        try
        {
            var reply = JsonSerializer.Deserialize<SignSaveReply>(data);
            if (reply?.RequestId != null && pendingSignSaves.Remove(reply.RequestId))
                clientApi.ShowChatMessage("Sign Fonts: " + (reply.Success ? "formatting saved on server." : "formatting was NOT saved. " + reply.Message));
        }
        catch (JsonException) { }
        return false;
    }
}
