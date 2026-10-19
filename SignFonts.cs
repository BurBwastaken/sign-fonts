using System.Reflection;
using System.Text.Json;
using HarmonyLib;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;

namespace SignFonts;

public sealed partial class SignFontsModSystem : ModSystem
{
    private const string HarmonyId = "ewant.signfonts";
    private const string ConfigFile = "signfonts.json";
    private Harmony harmony;
    private bool isClientInstance;
    private WindowsFontCatalog fontCatalog;
    private static string fontFamily = "Georgia";
    private static string[] fontFamilies = { "Georgia", "Arial", "Times New Roman", "Courier New", "Verdana" };
    private static string[] configuredFontFamilies = { "Georgia", "Arial", "Times New Roman", "Courier New", "Verdana" };
    private static Dictionary<string, string> signFonts = new(StringComparer.OrdinalIgnoreCase);
    private static Dictionary<string, string> legacySignFonts = new(StringComparer.OrdinalIgnoreCase);
    private static ICoreClientAPI clientApi;

    public override bool ShouldLoad(EnumAppSide forSide) => true;

    public override void StartClientSide(ICoreClientAPI api)
    {
        isClientInstance = true;
        clientApi = api;
        pendingSignSaves.Clear();
        signFonts = new(StringComparer.OrdinalIgnoreCase);
        legacySignFonts = new(StringComparer.OrdinalIgnoreCase);
        localFormatting = new(StringComparer.OrdinalIgnoreCase);
        var config = api.LoadModConfig(ConfigFile);
        if (config == null)
        {
            config = JsonObject.FromJson(JsonSerializer.Serialize(new { fontFamily, fontFamilies = configuredFontFamilies }));
            api.StoreModConfig(config, ConfigFile);
        }

        fontFamily = config["fontFamily"].AsString("Georgia");
        if (string.IsNullOrWhiteSpace(fontFamily)) fontFamily = "Georgia";
        try
        {
            var configuredFamilies = config["fontFamilies"].AsArray<string>();
            if (configuredFamilies?.Length > 0) configuredFontFamilies = configuredFamilies;
        }
        catch { }
        try
        {
            var legacy = config["signFonts"].Token?.ToString();
            if (!string.IsNullOrWhiteSpace(legacy))
                legacySignFonts = JsonSerializer.Deserialize<Dictionary<string, string>>(legacy) ?? legacySignFonts;
            var configuredSignFonts = config["signFontsV2"].Token?.ToString();
            if (!string.IsNullOrWhiteSpace(configuredSignFonts))
                signFonts = JsonSerializer.Deserialize<Dictionary<string, string>>(configuredSignFonts) ?? signFonts;
            var richConfig = config["signFormattingV1"].Token?.ToString();
            if (!string.IsNullOrWhiteSpace(richConfig))
                localFormatting = JsonSerializer.Deserialize<Dictionary<string, string>>(richConfig) ?? localFormatting;
        }
        catch { }
        var installedFamilies = Array.Empty<string>();
        var fontsDirectory = Path.Combine(GamePaths.ModConfig, "SignFonts", "Fonts");
        fontCatalog = new WindowsFontCatalog();
        try
        {
            Directory.CreateDirectory(fontsDirectory);
            foreach (var failedPath in fontCatalog.RegisterDirectory(fontsDirectory))
                Mod.Logger.Warning("SignFonts: Windows could not load font file '{0}'.", Path.GetFileName(failedPath));
            installedFamilies = WindowsFontCatalog.GetInstalledFamilies();
        }
        catch (Exception error)
        {
            Mod.Logger.Warning("SignFonts: automatic font discovery failed; configured choices remain available: {0}", error.Message);
        }
        fontFamilies = MergeFontFamilies(configuredFontFamilies, installedFamilies, new[] { fontFamily }.Concat(signFonts.Values));

        harmony = new Harmony(HarmonyId + ".client");
        PatchSignType(harmony, "Vintagestory.GameContent.BlockEntitySign");
        PatchSignType(harmony, "Vintagestory.GameContent.BlockEntitySignPost");
        PatchSignDialog(harmony, "GuiDialogBlockEntityTextInput");
        PatchSignDialog(harmony, "GuiDialogSignPost");
        var tryOpen = AccessTools.Method(typeof(GuiDialog), "TryOpen", Type.EmptyTypes);
        if (tryOpen != null)
            harmony.Patch(tryOpen, postfix: new HarmonyMethod(typeof(SignFontsModSystem), nameof(AfterDialogOpened)));
        InstallFontMenuPatches(harmony);
        InstallRichPatches(harmony);
        Mod.Logger.Notification("SignFonts: {0} font families available; last selection '{1}'. Custom font folder: {2}", fontFamilies.Length, fontFamily, fontsDirectory);
    }

    internal static string[] MergeFontFamilies(params IEnumerable<string>[] sources) => sources
        .SelectMany(source => source)
        .Where(family => !string.IsNullOrWhiteSpace(family))
        .Select(family => family.Trim())
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .OrderBy(family => family, StringComparer.OrdinalIgnoreCase)
        .ToArray();

    private void PatchSignType(Harmony harmony, string typeName)
    {
        var type = AccessTools.TypeByName(typeName);
        var initialize = type == null ? null : AccessTools.Method(type, "Initialize", new[] { typeof(ICoreAPI) });
        if (initialize == null)
        {
            Mod.Logger.Warning("SignFonts: could not find {0}.Initialize; this sign type will keep its default font.", typeName);
            return;
        }

        harmony.Patch(initialize, postfix: new HarmonyMethod(typeof(SignFontsModSystem), nameof(AfterSignInitialize)));
        var read = AccessTools.Method(type, "FromTreeAttributes", new[] { typeof(ITreeAttribute), typeof(IWorldAccessor) });
        if (read != null)
            harmony.Patch(read, postfix: new HarmonyMethod(typeof(SignFontsModSystem), nameof(AfterClientSignRead)));
        var received = AccessTools.Method(type, "OnReceivedServerPacket");
        if (received != null)
            harmony.Patch(received, prefix: new HarmonyMethod(typeof(SignFontsModSystem), nameof(BeforeSignSaveAcknowledgement)));
    }

    private void PatchSignDialog(Harmony harmony, string simpleTypeName)
    {
        var dialogType = AppDomain.CurrentDomain.GetAssemblies()
            .Select(assembly => AccessTools.TypeByName(assembly.GetName().Name + "." + simpleTypeName))
            .FirstOrDefault(type => type != null);

        // Search all loaded assemblies as the vanilla text dialog can live in either the API or game assembly.
        dialogType ??= AppDomain.CurrentDomain.GetAssemblies()
            .SelectMany(assembly =>
            {
                try { return assembly.GetTypes(); }
                catch (ReflectionTypeLoadException error) { return error.Types.Where(type => type != null); }
                catch { return Array.Empty<Type>(); }
            })
            .FirstOrDefault(type => type.Name == simpleTypeName);

        if (dialogType == null)
        {
            Mod.Logger.Warning("SignFonts: could not find the {0} sign editor; its font picker will be unavailable.", simpleTypeName);
            return;
        }

        foreach (var constructor in dialogType.GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
        {
            if (!constructor.GetParameters().Any(parameter => parameter.ParameterType == typeof(BlockPos))) continue;
            harmony.Patch(constructor, postfix: new HarmonyMethod(typeof(SignFontsModSystem), nameof(AfterSignDialogConstructed)));
        }
        var save = AccessTools.Method(dialogType, "OnButtonSave");
        if (save != null)
            harmony.Patch(save,
                prefix: new HarmonyMethod(typeof(SignFontsModSystem), nameof(BeforeSignSave)),
                postfix: new HarmonyMethod(typeof(SignFontsModSystem), nameof(AfterSignSave)));
    }

    private static void AfterSignDialogConstructed(object __instance, object[] __args)
    {
        if (clientApi == null) return;
        var position = __args.OfType<BlockPos>().FirstOrDefault();
        if (!__args.Any(argument => argument is BlockPos)) return;
        var blockEntity = clientApi.World.BlockAccessor.GetBlockEntity(position);
        ApplyFont(blockEntity, FontForEntity(blockEntity));
    }

    private static void AfterDialogOpened(object __instance, bool __result)
    {
        if (!__result || __instance == null) return;
        var typeName = __instance.GetType().Name;
        if (typeName != "GuiDialogBlockEntityTextInput" && typeName != "GuiDialogSignPost") return;

        try
        {
            var position = FindDialogPosition(__instance);
            if (position == null) return;
            var blockEntity = clientApi.World.BlockAccessor.GetBlockEntity(position);
            var selectedFont = FontForEntity(blockEntity);
            ApplyFont(blockEntity, selectedFont);

            var composer = AccessTools.Property(__instance.GetType(), "SingleComposer")?.GetValue(__instance) as GuiComposer;
            if (composer == null || !composer.Composed || composer.LastAddedElementBounds == null || composer.GetElement("signfonts-picker") != null) return;

            var session = new SignEditSession(blockEntity, selectedFont);
            editSessions.Remove(__instance);
            editSessions.Add(__instance, session);
            if (__instance is GuiDialog dialog)
                dialog.OnClosed += () =>
                {
                    if (!session.Saving)
                    {
                        fontStates.GetOrCreateValue(blockEntity).Formatting = session.OriginalFormatting;
                        ApplyFont(blockEntity, session.OriginalFamily);
                        RefreshFormattedSign(blockEntity);
                    }
                };

            PrepareRichEditor(composer, session);

            var choices = new[] { DefaultFontCode }.Concat(MergeFontFamilies(fontFamilies, new[] { selectedFont })).ToArray();
            var selectedIndex = Array.FindIndex(choices, family => string.Equals(family, selectedFont ?? DefaultFontCode, StringComparison.OrdinalIgnoreCase));
            AddFontPickerControls(composer, choices, Math.Max(0, selectedIndex), (family, active) =>
                {
                    if (!active || string.IsNullOrWhiteSpace(family)) return;
                    if (FormatSelectedFont(session, family)) return;
                    session.DraftFamily = family == DefaultFontCode ? null : family;
                    ApplyFont(blockEntity, session.DraftFamily);
                    RefreshFormattedSign(blockEntity);
                });
            AddRichControls(composer, session);
            composer.ReCompose();

            clientApi.Logger.Notification("SignFonts: added font picker to {0} at {1}.", typeName, position);
        }
        catch (Exception error)
        {
            clientApi?.Logger.Warning("SignFonts: could not add a font picker to the sign editor: {0}", error);
        }
    }

    internal static void AddFontPickerControls(GuiComposer composer, string[] families, int selectedIndex, SelectionChangedDelegate onSelectionChanged)
    {
        var anchor = composer.LastAddedElementBounds;
        var parent = anchor.ParentBounds ?? composer.Bounds;
        var labelBounds = ElementBounds.Fixed(0, anchor.fixedY + anchor.fixedHeight + 8, 250, 20);
        var pickerBounds = ElementBounds.Fixed(0, anchor.fixedY + anchor.fixedHeight + 30, 250, 28);

        // Vanilla's GUI helpers ignore additions while Composed is true.
        // Re-enter the original content parent so its background grows around the new row.
        composer.Composed = false;
        composer.BeginChildElements(parent);
        try
        {
            composer
                .AddStaticText("Sign font", CairoFont.WhiteDetailText(), labelBounds, "signfonts-label")
                .AddDropDown(families, families.Select(FormatFontLabel).ToArray(), selectedIndex, onSelectionChanged, pickerBounds, "signfonts-picker");
        }
        finally
        {
            composer.EndChildElements();
        }

        if (composer.GetElement("signfonts-picker") is not GuiElementDropDown picker)
            throw new InvalidOperationException("The sign font dropdown was not registered in the editor.");
        RegisterFontMenu(picker, families);
    }

    private static BlockPos FindDialogPosition(object dialog)
    {
        for (var type = dialog.GetType(); type != null; type = type.BaseType)
        {
            foreach (var field in type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
            {
                if (field.FieldType == typeof(BlockPos) && field.GetValue(dialog) is BlockPos position) return position;
            }
        }
        return null;
    }

    private static void AfterSignInitialize(object __instance)
    {
        ApplyFont(__instance, FontForEntity(__instance));
        if (__instance is BlockEntity entity) RefreshFormattedSign(entity);
    }

    private static void ApplyFont(object __instance, string family)
    {
        if (__instance == null) return;
        var entityType = __instance.GetType();
        var rendererField = FindField(entityType, "signRenderer");
        var renderer = rendererField?.GetValue(__instance);
        if (renderer == null) return;
        BindFormattedRenderer((BlockEntity)__instance, renderer);

        var fontField = FindField(renderer.GetType(), "font");
        var oldFont = fontField?.GetValue(renderer) as CairoFont;
        if (fontField == null || oldFont == null) return;

        var state = fontStates.GetOrCreateValue((BlockEntity)__instance);
        state.VanillaFamily ??= oldFont.Fontname;
        family ??= state.VanillaFamily;
        if (string.Equals(oldFont.Fontname, family, StringComparison.OrdinalIgnoreCase)) return;

        var font = oldFont.Clone();
        font.Fontname = family;
        fontField.SetValue(renderer, font);

        // Vanilla keeps unsaved edits in the renderer while the block entity still
        // contains the last saved text. Reuse the live preview, including an empty draft.
        var rendererType = renderer.GetType();
        var text = FindField(rendererType, "text")?.GetValue(renderer)
            ?? FindField(rendererType, "textByCardinal")?.GetValue(renderer)
            ?? FindField(entityType, "text")?.GetValue(__instance)
            ?? FindField(entityType, "textByCardinalDirection")?.GetValue(__instance);
        var color = FindField(entityType, "color")?.GetValue(__instance) as int? ?? ColorUtil.BlackArgb;
        if (oldFont.Color is { Length: >= 4 } rgba)
        {
            static int Channel(double value) => (int)Math.Round(Math.Clamp(value, 0, 1) * 255);
            color = ColorUtil.ToRgba(Channel(rgba[3]), Channel(rgba[0]), Channel(rgba[1]), Channel(rgba[2]));
        }
        var setNewText = AccessTools.Method(renderer.GetType(), "SetNewText");
        if (text != null && setNewText != null) setNewText.Invoke(renderer, new[] { text, (object)color });
    }

    private static void StoreConfig()
    {
        clientApi?.StoreModConfig(JsonObject.FromJson(JsonSerializer.Serialize(new { fontFamily, fontFamilies = configuredFontFamilies, signFonts = legacySignFonts, signFontsV2 = signFonts, signFormattingV1 = localFormatting })), ConfigFile);
    }

    private static string FontForEntity(object entity)
    {
        if (entity is not BlockEntity blockEntity) return null;
        var state = fontStates.GetOrCreateValue(blockEntity);
        if (state.ServerManaged) return state.SelectedFamily;
        var key = PositionKey(blockEntity.Pos);
        return key != null && signFonts.TryGetValue(key, out var family) ? family : null;
    }

    private static string PositionKey(BlockPos position) => position == null ? null : BuildPositionKey(clientApi?.World?.SavegameIdentifier, position.dimension, position.X, position.Y, position.Z);

    internal static string BuildPositionKey(string worldId, int dimension, int x, int y, int z) =>
        string.IsNullOrWhiteSpace(worldId) ? null : $"{Uri.EscapeDataString(worldId)}:{dimension}:{x},{y},{z}";

    private static FieldInfo FindField(Type type, string name)
    {
        for (var current = type; current != null; current = current.BaseType)
        {
            var field = current.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
            if (field != null) return field;
        }
        return null;
    }

    public override void Dispose()
    {
        StopPersistentSignStorage();
        if (harmony != null) harmony.UnpatchAll(harmony.Id);
        harmony = null;
        fontCatalog?.Dispose();
        fontCatalog = null;
        if (isClientInstance) RemoveFontLabelConverter();
    }
}
