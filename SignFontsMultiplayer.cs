using System.Runtime.CompilerServices;
using System.Text;
using HarmonyLib;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Datastructures;
using Vintagestory.API.Server;

namespace SignFonts;

public sealed partial class SignFontsModSystem
{
    private const string DefaultFontCode = "__game_default__";
    private const string FamilyAttribute = "signfonts:family";
    private const string ManagedAttribute = "signfonts:managed";
    private const int FontPacketId = 48723;
    private static readonly ConditionalWeakTable<BlockEntity, SignFontState> fontStates = new();
    private static readonly ConditionalWeakTable<object, SignEditSession> editSessions = new();

    private sealed class SignFontState
    {
        public string VanillaFamily;
        public string SelectedFamily;
        public bool ServerManaged;
        public SignFormatting Formatting;
    }

    private sealed class SignEditSession(BlockEntity entity, string family)
    {
        public readonly BlockEntity Entity = entity;
        public readonly string OriginalFamily = family;
        public string DraftFamily = family;
        public bool Saving;
        public SignFormatting OriginalFormatting;
        public SignFormatting DraftFormatting;
        public readonly List<RichTextField> TextFields = new();
        public int ActiveField;
        public GuiElementDropDown AlignmentPicker;
    }

    public override void StartServerSide(ICoreServerAPI api)
    {
        StartPersistentSignStorage(api);
        harmony = new Harmony(HarmonyId + ".server");
        InstallServerPatches(harmony);
        Mod.Logger.Notification("SignFonts 1.3.2: server support active; formatting uses sign data and a world-save backup, with save acknowledgements.");
    }

    internal static void InstallServerPatches(Harmony harmony)
    {
        foreach (var name in new[] { "Vintagestory.GameContent.BlockEntitySign", "Vintagestory.GameContent.BlockEntitySignPost" })
        {
            var type = AccessTools.TypeByName(name) ?? throw new InvalidOperationException($"Missing vanilla sign class {name}.");
            harmony.Patch(AccessTools.Method(type, "ToTreeAttributes"),
                postfix: new HarmonyMethod(typeof(SignFontsModSystem), nameof(AfterServerSignWrite)));
            harmony.Patch(AccessTools.Method(type, "FromTreeAttributes"),
                postfix: new HarmonyMethod(typeof(SignFontsModSystem), nameof(AfterServerSignRead)));
            harmony.Patch(AccessTools.Method(type, "OnReceivedClientPacket"),
                prefix: new HarmonyMethod(typeof(SignFontsModSystem), nameof(BeforeSignClientPacket)));
            harmony.Patch(AccessTools.Method(type, "Initialize"),
                postfix: new HarmonyMethod(typeof(SignFontsModSystem), nameof(AfterServerSignInitialize)));
            var removed = AccessTools.Method(type, "OnBlockRemoved");
            if (removed != null && Harmony.GetPatchInfo(removed)?.Owners.Contains(harmony.Id) != true)
                harmony.Patch(removed, prefix: new HarmonyMethod(typeof(SignFontsModSystem), nameof(BeforeStoredSignRemoved)));
        }
    }

    private static void AfterServerSignWrite(object __instance, ITreeAttribute tree)
    {
        if (__instance is not BlockEntity entity || entity.Api?.Side == EnumAppSide.Client) return;
        // Chunk snapshots can be serialised before Initialize supplies Api.
        if (entity.Api == null && !fontStates.GetOrCreateValue(entity).ServerManaged) return;
        WriteFontAttributes(tree, fontStates.GetOrCreateValue(entity).SelectedFamily);
        tree.SetString(FormattingAttribute, fontStates.GetOrCreateValue(entity).Formatting?.Serialize() ?? "");
    }

    internal static void WriteFontAttributes(ITreeAttribute tree, string family)
    {
        tree.SetBool(ManagedAttribute, true);
        if (family == null) tree.RemoveAttribute(FamilyAttribute);
        else tree.SetString(FamilyAttribute, family);
    }

    private static void AfterServerSignRead(object __instance, ITreeAttribute tree, IWorldAccessor worldForResolving)
    {
        if (worldForResolving.Side != EnumAppSide.Server || __instance is not BlockEntity entity) return;
        ReadSignFont(entity, tree);
        fontStates.GetOrCreateValue(entity).ServerManaged = true;
    }

    private static void AfterClientSignRead(object __instance, ITreeAttribute tree, IWorldAccessor worldForResolving)
    {
        if (worldForResolving.Side != EnumAppSide.Client || __instance is not BlockEntity entity) return;
        ReadSignFont(entity, tree);
        ApplyFont(entity, FontForEntity(entity));
        RefreshFormattedSign(entity);
    }

    internal static void ReadSignFont(BlockEntity entity, ITreeAttribute tree)
    {
        var state = fontStates.GetOrCreateValue(entity);
        state.ServerManaged = tree.GetBool(ManagedAttribute, false);
        state.SelectedFamily = tree.GetString(FamilyAttribute, null);
        try { state.Formatting = SignFormatting.Parse(tree.GetString(FormattingAttribute, "")); }
        catch (Exception error)
        {
            state.Formatting = null;
            (entity.Api?.Logger ?? persistentServerApi?.Logger)?.Warning("SignFonts: could not read formatting at {0}: {1}", entity.Pos, error.Message);
        }
    }

    private static void BeforeSignSave(object __instance)
    {
        if (editSessions.TryGetValue(__instance, out var session))
        {
            SynchronizeDraftText(session);
            session.Saving = true;
        }
    }

    private static void AfterSignSave(object __instance, bool __result)
    {
        if (!editSessions.TryGetValue(__instance, out var session)) return;
        if (!__result)
        {
            session.Saving = false;
            fontStates.GetOrCreateValue(session.Entity).Formatting = session.OriginalFormatting;
            ApplyFont(session.Entity, session.OriginalFamily);
            RefreshFormattedSign(session.Entity);
            return;
        }

        var state = fontStates.GetOrCreateValue(session.Entity);
        state.SelectedFamily = session.DraftFamily;
        state.Formatting = session.DraftFormatting;
        // Only servers without Sign Fonts need a client-local fallback.
        var key = PositionKey(session.Entity.Pos);
        if (!state.ServerManaged && key != null)
        {
            if (session.DraftFamily == null) signFonts.Remove(key);
            else signFonts[key] = session.DraftFamily;
            var rich = session.DraftFormatting.Serialize();
            if (rich.Length == 0) localFormatting.Remove(key);
            else localFormatting[key] = rich;
        }
        if (session.DraftFamily != null) fontFamily = session.DraftFamily;
        StoreConfig();
        SendConfirmedSignSave(session, __instance);
    }

    private static bool BeforeSignClientPacket(object __instance, IPlayer player, int packetid, byte[] data)
    {
        if (packetid == ConfirmedSavePacketId) return ReceiveConfirmedSignSave(__instance, player, data);
        if (packetid == FormattingPacketId) return ReceiveFormatting(__instance, player, data);
        if (packetid != FontPacketId) return true;
        if (__instance is not BlockEntity entity || entity.Api?.Side != EnumAppSide.Server) return false;
        if (data == null || data.Length > 512) return false;
        var family = Encoding.UTF8.GetString(data);
        if (family.Length > 128 || family.Any(char.IsControl)) return false;
        // Use the same reach/claim check as vanilla 1.22 signs. Revisit for the 1.23 API.
#pragma warning disable CS0618
        var permissions = new BlockEntity.CachedAccessPerms(entity.Api.World, entity.Pos, player);
#pragma warning restore CS0618
        if (!permissions.IsInteractingPlayerAllowedTo(EnumBlockAccessFlags.Use, true, "signfonts"))
        {
            entity.MarkDirty(true);
            return false;
        }

        var state = fontStates.GetOrCreateValue(entity);
        state.SelectedFamily = string.IsNullOrWhiteSpace(family) ? null : family;
        state.ServerManaged = true;
        RememberServerSign(entity);
        entity.MarkDirty(true);
        entity.Api.World.BlockAccessor.GetChunkAtBlockPos(entity.Pos)?.MarkModified();
        return false;
    }
}
