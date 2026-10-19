using System.Globalization;
using System.Runtime.CompilerServices;
using Cairo;
using HarmonyLib;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;

namespace SignFonts;

public sealed partial class SignFontsModSystem
{
    private sealed class RendererOwner(BlockEntity entity)
    {
        public readonly BlockEntity Entity = entity;
        public bool ReportedError;
    }
    private static readonly ConditionalWeakTable<object, RendererOwner> rendererOwners = new();

    private static void InstallRichPatches(Harmony harmony)
    {
        harmony.Patch(AccessTools.Method(typeof(GuiElementEditableTextBase), "OnFocusLost"),
            prefix: new HarmonyMethod(typeof(SignFontsModSystem), nameof(CaptureSelection)));
        harmony.Patch(AccessTools.Method(typeof(GuiElementEditableTextBase), "OnFocusGained"),
            postfix: new HarmonyMethod(typeof(SignFontsModSystem), nameof(CaptureSelection)));
        var normal = AccessTools.TypeByName("Vintagestory.GameContent.BlockEntitySignRenderer");
        harmony.Patch(AccessTools.Method(normal, "RenderText"),
            prefix: new HarmonyMethod(typeof(SignFontsModSystem), nameof(RenderFormattedText)));
        var post = AccessTools.TypeByName("Vintagestory.GameContent.BlockEntitySignPostRenderer");
        harmony.Patch(AccessTools.Method(post, "SetNewText"),
            prefix: new HarmonyMethod(typeof(SignFontsModSystem), nameof(RenderFormattedPost)));
    }

    private static void BindFormattedRenderer(BlockEntity entity, object renderer)
    {
        if (!rendererOwners.TryGetValue(renderer, out _)) rendererOwners.Add(renderer, new RendererOwner(entity));
        var state = fontStates.GetOrCreateValue(entity);
        if (!state.ServerManaged && state.Formatting == null && PositionKey(entity.Pos) is { } key && localFormatting.TryGetValue(key, out var stored))
        {
            try { state.Formatting = SignFormatting.Parse(stored); }
            catch { localFormatting.Remove(key); }
        }
    }

    private static void RefreshFormattedSign(BlockEntity entity)
    {
        var renderer = entity == null ? null : FindField(entity.GetType(), "signRenderer")?.GetValue(entity);
        if (renderer == null) return;
        BindFormattedRenderer(entity, renderer);
        var text = FindField(renderer.GetType(), "text")?.GetValue(renderer) ?? FindField(renderer.GetType(), "textByCardinal")?.GetValue(renderer);
        var font = FindField(renderer.GetType(), "font")?.GetValue(renderer) as CairoFont;
        if (text == null || font == null) return;
        var color = font.Color is { Length: >= 4 } rgba
            ? ColorUtil.ToRgba((int)(rgba[3] * 255), (int)(rgba[0] * 255), (int)(rgba[1] * 255), (int)(rgba[2] * 255))
            : ColorUtil.BlackArgb;
        AccessTools.Method(renderer.GetType(), "SetNewText").Invoke(renderer, new[] { text, (object)color });
    }

    private static FormattedField MatchingField(object renderer, int index, string text)
    {
        if (!rendererOwners.TryGetValue(renderer, out var owner)) return null;
        var doc = fontStates.GetOrCreateValue(owner.Entity).Formatting;
        if (doc == null || !doc.HasFormatting || index >= doc.Fields.Count) return null;
        var field = doc.Fields[index];
        // An edit from vanilla or another mod must never apply stale character ranges.
        return field.Text == text ? field : null;
    }

    private static bool RenderFormattedText(object __instance, ref LoadedTexture __result)
    {
        var type = __instance.GetType();
        var text = FindField(type, "text")?.GetValue(__instance) as string;
        var field = MatchingField(__instance, 0, text);
        if (field == null) return true;
        try
        {
            var api = (ICoreClientAPI)FindField(type, "api").GetValue(__instance);
            var font = (CairoFont)FindField(type, "font").GetValue(__instance);
            var size = Convert.ToDouble(FindField(type, "fontSize").GetValue(__instance));
            var width = (int)FindField(type, "TextWidth").GetValue(__instance);
            var height = (int)FindField(type, "TextHeight").GetValue(__instance);
            var centerVertically = Convert.ToInt32(FindField(type, "verticalAlign").GetValue(__instance)) == 1;
            using var surface = new ImageSurface(Format.Argb32, width, height);
            using var context = new Context(surface);
            DrawFormattedField(context, field, font, size, width, height, centerVertically);
            var texture = new LoadedTexture(api);
            try { api.Gui.LoadOrUpdateCairoTexture(surface, true, ref texture); }
            catch { texture.Dispose(); throw; }
            __result = texture;
            return false;
        }
        catch (Exception error)
        {
            ReportRenderError(__instance, error);
            return true;
        }
    }

    private static bool RenderFormattedPost(object __instance, string[] textByCardinal, int color)
    {
        if (textByCardinal == null || !textByCardinal.Where((t, i) => MatchingField(__instance, i, t) != null).Any()) return true;
        try
        {
            var type = __instance.GetType();
            var api = (ICoreClientAPI)FindField(type, "api").GetValue(__instance);
            var font = (CairoFont)FindField(type, "font").GetValue(__instance);
            font.WithColor(ColorUtil.ToRGBADoubles(color));
            var size = Convert.ToDouble(FindField(type, "fontSize").GetValue(__instance));
            var width = (int)FindField(type, "TextWidth").GetValue(__instance);
            var height = (int)FindField(type, "TextHeight").GetValue(__instance);
            var count = textByCardinal.Count(t => !string.IsNullOrEmpty(t));
            if (count == 0) return true;
            using var surface = new ImageSurface(Format.Argb32, width, height * count);
            using var context = new Context(surface);
            var row = 0;
            for (var i = 0; i < textByCardinal.Length; i++)
            {
                if (string.IsNullOrEmpty(textByCardinal[i])) continue;
                context.Save();
                context.Translate(0, row++ * height);
                DrawFormattedField(context, MatchingField(__instance, i, textByCardinal[i]) ?? new FormattedField { Text = textByCardinal[i] }, font, size, width, height, false);
                context.Restore();
            }
            var textureField = FindField(type, "loadedTexture");
            var texture = textureField.GetValue(__instance) as LoadedTexture ?? new LoadedTexture(api);
            api.Gui.LoadOrUpdateCairoTexture(surface, true, ref texture);
            textureField.SetValue(__instance, texture);
            FindField(type, "textByCardinal").SetValue(__instance, textByCardinal);
            AccessTools.Method(type, "genMesh").Invoke(__instance, null);
            return false;
        }
        catch (Exception error)
        {
            ReportRenderError(__instance, error);
            return true;
        }
    }

    private static void ReportRenderError(object renderer, Exception error)
    {
        if (!rendererOwners.TryGetValue(renderer, out var owner) || owner.ReportedError) return;
        owner.ReportedError = true;
        owner.Entity.Api?.Logger.Warning("SignFonts: formatting renderer fell back to plain text: {0}", error.Message);
    }

    private sealed record Glyph(string Text, LetterStyle Style, double Width, double Ascent, double Descent);
    private sealed class SignLine
    {
        public string Alignment;
        public readonly List<Glyph> Glyphs = new();
        public double Width;
        public double Ascent;
        public double Descent;
    }

    private static void SelectStyle(Context context, CairoFont font, double baseSize, LetterStyle style)
    {
        context.SelectFontFace(style.Family ?? font.Fontname,
            style.Italic.HasValue ? style.Italic.Value ? FontSlant.Italic : FontSlant.Normal : font.Slant,
            style.Bold.HasValue ? style.Bold.Value ? FontWeight.Bold : FontWeight.Normal : font.FontWeight);
        context.SetFontSize(style.Size == 0 ? baseSize : style.Size);
    }

    private static void DrawFormattedField(Context context, FormattedField field, CairoFont font, double baseSize, int width, int height, bool centerVertically)
    {
        context.Rectangle(0, 0, width, height);
        context.Clip();
        var styles = field.Expand();
        var lines = new List<SignLine>();
        var paragraph = 0;
        var current = new SignLine { Alignment = field.AlignmentAtLine(paragraph) };
        void AddLine()
        {
            if (current.Glyphs.Count == 0)
            {
                SelectStyle(context, font, baseSize, new LetterStyle());
                current.Ascent = context.FontExtents.Ascent;
                current.Descent = context.FontExtents.Descent;
            }
            lines.Add(current);
            current = new SignLine { Alignment = field.AlignmentAtLine(paragraph) };
        }
        var elements = StringInfo.GetTextElementEnumerator(field.Text);
        var word = new List<Glyph>();
        void FlushWord()
        {
            var wordWidth = word.Sum(g => g.Width);
            if (current.Glyphs.Count > 0 && current.Width + wordWidth > width) AddLine();
            foreach (var glyph in word)
            {
                if (current.Glyphs.Count > 0 && current.Width + glyph.Width > width) AddLine();
                current.Glyphs.Add(glyph);
                current.Width += glyph.Width;
                current.Ascent = Math.Max(current.Ascent, glyph.Ascent);
                current.Descent = Math.Max(current.Descent, glyph.Descent);
            }
            word.Clear();
        }
        while (elements.MoveNext())
        {
            var text = elements.GetTextElement();
            if (text == "\n" || text == "\r\n")
            {
                FlushWord();
                AddLine();
                current.Alignment = field.AlignmentAtLine(++paragraph);
                continue;
            }
            if (text == "\r") continue;
            var style = styles[elements.ElementIndex];
            SelectStyle(context, font, baseSize, style);
            var whitespace = string.IsNullOrWhiteSpace(text);
            if (whitespace) FlushWord();
            var glyph = new Glyph(text == "\t" ? "    " : text, style, context.TextExtents(text == "\t" ? "    " : text).XAdvance, context.FontExtents.Ascent, context.FontExtents.Descent);
            if (whitespace)
            {
                // Spaces at the right margin become a line break, not a new empty line.
                if (current.Width + glyph.Width <= width)
                {
                    current.Glyphs.Add(glyph);
                    current.Width += glyph.Width;
                    current.Ascent = Math.Max(current.Ascent, glyph.Ascent);
                    current.Descent = Math.Max(current.Descent, glyph.Descent);
                }
                else AddLine();
            }
            else word.Add(glyph);
        }
        FlushWord();
        AddLine();
        var totalHeight = lines.Sum(l => l.Ascent + l.Descent);
        var y = centerVertically ? Math.Max(0, (height - totalHeight) / 2) : 0;
        var rgba = font.Color ?? new[] { 0.0, 0, 0, 1 };
        context.SetSourceRGBA(rgba[0], rgba[1], rgba[2], rgba.Length > 3 ? rgba[3] : 1);
        foreach (var line in lines)
        {
            var x = line.Alignment == "left" ? 0 : line.Alignment == "right" ? width - line.Width : (width - line.Width) / 2;
            var baseline = y + line.Ascent;
            foreach (var glyph in line.Glyphs)
            {
                SelectStyle(context, font, baseSize, glyph.Style);
                context.MoveTo(x, baseline);
                context.ShowText(glyph.Text);
                if (glyph.Style.Underline)
                {
                    context.LineWidth = Math.Max(1, (glyph.Style.Size == 0 ? baseSize : glyph.Style.Size) / 16);
                    context.MoveTo(x, baseline + 1.5);
                    context.LineTo(x + glyph.Width, baseline + 1.5);
                    context.Stroke();
                }
                x += glyph.Width;
            }
            y += line.Ascent + line.Descent;
            if (y >= height) break;
        }
    }
}
