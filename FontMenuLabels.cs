using System.Runtime.CompilerServices;
using System.Text;
using HarmonyLib;
using Vintagestory.API.Client;
using Vintagestory.API.Common;

namespace SignFonts;

public sealed partial class SignFontsModSystem
{
    private sealed record FontMenuNames(string[] Names);
    private const string FontPreviewTag = "signfontsfont";
    private static readonly ConditionalWeakTable<GuiElementListMenu, FontMenuNames> fontMenus = new();

    internal static string FormatFontLabel(string family)
    {
        if (family == DefaultFontCode) return "Default (game font)";
        var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(family));
        return $"<{FontPreviewTag} family=\"{encoded}\"></{FontPreviewTag}>";
    }

    internal static string DecodeFontLabel(VtmlTagToken token) => Encoding.UTF8.GetString(Convert.FromBase64String(token.Attributes["family"]));

    internal static CairoFont CreateFontLabelFont(CairoFont baseFont, string family)
    {
        var font = baseFont.Clone();
        font.Fontname = family;
        return font;
    }

    private static RichTextComponentBase FontLabelComponent(ICoreClientAPI api, VtmlTagToken token, Stack<CairoFont> fonts, Action<LinkTextComponent> onClick)
    {
        var family = DecodeFontLabel(token);
        return new RichTextComponent(api, family, CreateFontLabelFont(fonts.Peek(), family));
    }

    private static void RegisterFontMenu(GuiElementDropDown picker, string[] families)
    {
        if (AccessTools.Field(typeof(GuiElementDropDown), "listMenu").GetValue(picker) is not GuiElementListMenu menu) return;
        fontMenus.Remove(menu);
        fontMenus.Add(menu, new FontMenuNames(families.Select(family => family == DefaultFontCode ? "Default (game font)" : family).ToArray()));
    }

    internal static void InstallFontMenuPatches(Harmony harmony)
    {
        VtmlUtil.TagConverters[FontPreviewTag] = FontLabelComponent;
        // Vanilla's keyboard jump searches display labels. Use plain names for that
        // operation, while retaining styled labels for rendering and selected values.
        harmony.Patch(AccessTools.Method(typeof(GuiElementListMenu), "OnKeyPress"),
            prefix: new HarmonyMethod(typeof(SignFontsModSystem), nameof(BeforeFontMenuKeyPress)),
            finalizer: new HarmonyMethod(typeof(SignFontsModSystem), nameof(AfterFontMenuKeyPress)));
    }

    private static void RemoveFontLabelConverter()
    {
        if (VtmlUtil.TagConverters.TryGetValue(FontPreviewTag, out var converter) && converter.Method.DeclaringType == typeof(SignFontsModSystem))
            VtmlUtil.TagConverters.Remove(FontPreviewTag);
    }

    private static void BeforeFontMenuKeyPress(GuiElementListMenu __instance, out string[] __state)
    {
        __state = null;
        if (!fontMenus.TryGetValue(__instance, out var names)) return;
        __state = __instance.Names;
        __instance.Names = names.Names;
    }

    private static void AfterFontMenuKeyPress(GuiElementListMenu __instance, string[] __state)
    {
        if (__state != null) __instance.Names = __state;
    }
}
