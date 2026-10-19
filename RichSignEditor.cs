using System.Runtime.CompilerServices;
using System.Text;
using HarmonyLib;
using Vintagestory.API.Client;
using Vintagestory.API.Common;

namespace SignFonts;

public sealed partial class SignFontsModSystem
{
    private const string FormattingAttribute = "signfonts:formatting";
    private const int FormattingPacketId = 48724;
    private static Dictionary<string, string> localFormatting = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConditionalWeakTable<GuiElementEditableTextBase, RichTextField> richFields = new();

    private sealed class RichTextField(SignEditSession session, GuiElementEditableTextBase input, int index)
    {
        public readonly SignEditSession Session = session;
        public readonly GuiElementEditableTextBase Input = input;
        public readonly int Index = index;
        public int Start;
        public int End;
    }

    private static void PrepareRichEditor(GuiComposer composer, SignEditSession session)
    {
        var state = fontStates.GetOrCreateValue(session.Entity);
        if (!state.ServerManaged && PositionKey(session.Entity.Pos) is { } key && localFormatting.TryGetValue(key, out var stored))
        {
            try { state.Formatting = SignFormatting.Parse(stored); }
            catch { state.Formatting = null; }
        }
        session.OriginalFormatting = state.Formatting?.Clone();
        session.DraftFormatting = state.Formatting?.Clone() ?? new SignFormatting();
        var inputs = composer.GetElement("text") is GuiElementEditableTextBase main
            ? new[] { main }
            : Enumerable.Range(0, 8).Select(i => composer.GetElement("text" + i)).OfType<GuiElementEditableTextBase>().ToArray();
        for (var i = 0; i < inputs.Length; i++)
        {
            var field = new RichTextField(session, inputs[i], i);
            session.TextFields.Add(field);
            richFields.Remove(inputs[i]);
            richFields.Add(inputs[i], field);
            while (session.DraftFormatting.Fields.Count <= i) session.DraftFormatting.Fields.Add(new FormattedField());
            session.DraftFormatting.Fields[i].Reconcile(inputs[i].GetText());
            var original = inputs[i].OnTextChanged;
            inputs[i].OnTextChanged = text =>
            {
                // Update ranges before vanilla calls the sign renderer for its preview.
                session.DraftFormatting.Fields[field.Index].Reconcile(text);
                state.Formatting = session.DraftFormatting;
                original?.Invoke(text);
            };
            var originalCaretChanged = inputs[i].OnCaretPositionChanged;
            inputs[i].OnCaretPositionChanged = (line, column) =>
            {
                originalCaretChanged?.Invoke(line, column);
                if (field.Input.HasFocus) CaptureSelection(field.Input);
            };
        }
        if (session.DraftFormatting.Fields.Count > inputs.Length)
            session.DraftFormatting.Fields.RemoveRange(inputs.Length, session.DraftFormatting.Fields.Count - inputs.Length);
        state.Formatting = session.DraftFormatting;
    }

    private static void CaptureSelection(GuiElementEditableTextBase __instance)
    {
        if (!richFields.TryGetValue(__instance, out var field)) return;
        var caret = __instance.CaretPosWithoutLineBreaks;
        var anchor = FindField(__instance.GetType(), "selectedTextStart")?.GetValue(__instance) as int? ?? caret;
        field.Start = Math.Min(anchor, caret);
        field.End = Math.Max(anchor, caret);
        field.Session.ActiveField = field.Index;
        var doc = field.Session.DraftFormatting.Fields[field.Index];
        var alignment = doc.AlignmentAtLine(doc.LineAt(field.Start));
        var picker = field.Session.AlignmentPicker;
        if (picker != null && picker.SelectedValue != alignment)
            picker.SetSelectedValue(alignment);
    }

    private static RichTextField CurrentField(SignEditSession session)
    {
        var focused = session.TextFields.FirstOrDefault(f => f.Input.HasFocus);
        if (focused != null) CaptureSelection(focused.Input);
        return session.TextFields.ElementAtOrDefault(session.ActiveField);
    }

    private static void SynchronizeDraftText(SignEditSession session)
    {
        foreach (var field in session.TextFields)
            session.DraftFormatting.Fields[field.Index].Reconcile(field.Input.GetText());
    }

    private static bool FormatSelectedFont(SignEditSession session, string family)
    {
        var field = CurrentField(session);
        if (field == null) return false;
        if (field.End <= field.Start && session.TextFields.Count == 1)
        {
            // A whole-sign font choice replaces earlier per-word font overrides.
            ChangeStyle(session, s => s with { Family = null });
            return false;
        }
        ChangeStyle(session, s => s with { Family = family == DefaultFontCode ? fontStates.GetOrCreateValue(session.Entity).VanillaFamily : family });
        return true;
    }

    private static bool ChangeStyle(SignEditSession session, System.Func<LetterStyle, LetterStyle> change)
    {
        var field = CurrentField(session);
        if (field == null) return true;
        SynchronizeDraftText(session);
        var doc = session.DraftFormatting.Fields[field.Index];
        var start = field.End > field.Start ? field.Start : 0;
        var end = field.End > field.Start ? field.End : doc.Text.Length;
        doc.Format(start, end, change);
        fontStates.GetOrCreateValue(session.Entity).Formatting = session.DraftFormatting;
        RefreshFormattedSign(session.Entity);
        return true;
    }

    private static bool ToggleStyle(SignEditSession session, string style)
    {
        var field = CurrentField(session);
        if (field == null) return true;
        var doc = session.DraftFormatting.Fields[field.Index];
        var styles = doc.Expand();
        var start = field.End > field.Start ? field.Start : 0;
        var end = field.End > field.Start ? field.End : styles.Length;
        var renderer = FindField(session.Entity.GetType(), "signRenderer")?.GetValue(session.Entity);
        var baseFont = renderer == null ? null : FindField(renderer.GetType(), "font")?.GetValue(renderer) as CairoFont;
        bool IsOn(LetterStyle s) => style == "bold" ? s.Bold ?? (baseFont?.FontWeight == Cairo.FontWeight.Bold)
            : style == "italic" ? s.Italic ?? (baseFont != null && baseFont.Slant != Cairo.FontSlant.Normal) : s.Underline;
        var next = !styles.Skip(start).Take(end - start).All(IsOn);
        return ChangeStyle(session, s => style == "bold" ? s with { Bold = next } : style == "italic" ? s with { Italic = next } : s with { Underline = next });
    }

    private static void AddRichControls(GuiComposer composer, SignEditSession session)
    {
        var anchor = composer.LastAddedElementBounds;
        var parent = anchor.ParentBounds ?? composer.Bounds;
        var y = anchor.fixedY + anchor.fixedHeight + 10;
        composer.Composed = false;
        composer.BeginChildElements(parent);
        try
        {
            composer.AddStaticText("Select text, then format. No selection: whole field.", CairoFont.WhiteDetailText().WithFontSize(11), ElementBounds.Fixed(0, y, 290, 18), "signfonts-format-help");
            y += 22;
            var sizes = new[] { "Base", "8", "10", "12", "14", "16", "18", "20", "24", "28", "32", "36", "40", "48", "60", "72" };
            composer.AddDropDown(sizes, sizes.Select(s => s == "Base" ? "Base size" : s + " pt").ToArray(), 0,
                (code, selected) => { if (selected) ChangeStyle(session, s => s with { Size = int.TryParse(code, out var size) ? size : 0 }); },
                ElementBounds.Fixed(0, y, 92, 28), "signfonts-word-size");
            composer.AddSmallButton("B", () => ToggleStyle(session, "bold"), ElementBounds.Fixed(100, y, 30, 24), EnumButtonStyle.Normal, "signfonts-bold");
            composer.AddSmallButton("I", () => ToggleStyle(session, "italic"), ElementBounds.Fixed(138, y, 30, 24), EnumButtonStyle.Normal, "signfonts-italic");
            composer.AddSmallButton("U", () => ToggleStyle(session, "underline"), ElementBounds.Fixed(176, y, 30, 24), EnumButtonStyle.Normal, "signfonts-underline");
            composer.AddSmallButton("Clear", () => ChangeStyle(session, _ => new LetterStyle()), ElementBounds.Fixed(214, y, 65, 24), EnumButtonStyle.Normal, "signfonts-clear");
            y += 36;
            composer.AddStaticText("Align lines", CairoFont.WhiteDetailText(), ElementBounds.Fixed(0, y + 3, 85, 22), "signfonts-align-label");
            var active = CurrentField(session);
            var activeDoc = session.DraftFormatting.Fields.ElementAtOrDefault(session.ActiveField);
            var alignment = activeDoc?.AlignmentAtLine(activeDoc.LineAt(active?.Start ?? 0)) ?? "center";
            var alignments = new[] { "left", "center", "right" };
            composer.AddDropDown(alignments, new[] { "Left", "Centre", "Right" }, Array.IndexOf(alignments, alignment),
                (code, selected) =>
                {
                    var field = CurrentField(session);
                    if (!selected || field == null) return;
                    SynchronizeDraftText(session);
                    session.DraftFormatting.Fields[field.Index].AlignSelection(field.Start, field.End, code);
                    fontStates.GetOrCreateValue(session.Entity).Formatting = session.DraftFormatting;
                    RefreshFormattedSign(session.Entity);
                }, ElementBounds.Fixed(92, y, 187, 28), "signfonts-alignment");
        }
        finally { composer.EndChildElements(); }
        session.AlignmentPicker = composer.GetElement("signfonts-alignment") as GuiElementDropDown;
        // The normal sign dialog uses a fixed background; expand it for all rows.
        parent.fixedHeight = Math.Max(parent.fixedHeight, y + 38);
        parent.fixedWidth = Math.Max(parent.fixedWidth, 290);
    }

    private static bool ReceiveFormatting(object instance, IPlayer player, byte[] data)
    {
        if (instance is not BlockEntity entity || entity.Api?.Side != EnumAppSide.Server) return false;
        if (data == null || data.Length > 131072) return false;
#pragma warning disable CS0618
        var permissions = new BlockEntity.CachedAccessPerms(entity.Api.World, entity.Pos, player);
#pragma warning restore CS0618
        if (!permissions.IsInteractingPlayerAllowedTo(EnumBlockAccessFlags.Use, true, "signfonts")) return false;
        try
        {
            var doc = SignFormatting.Parse(Encoding.UTF8.GetString(data));
            var actual = EntitySignText(entity);
            if (doc != null && (doc.Fields.Count != actual.Length || doc.Fields.Where((f, i) => f.Text != actual[i]).Any()))
            {
                entity.Api.Logger.Warning("SignFonts: rejected formatting at {0}: saved wording did not match the formatting packet.", entity.Pos);
                return false;
            }
            fontStates.GetOrCreateValue(entity).Formatting = doc;
            RememberServerSign(entity);
            entity.MarkDirty(true);
            entity.Api.World.BlockAccessor.GetChunkAtBlockPos(entity.Pos)?.MarkModified();
        }
        catch (Exception e) when (e is ArgumentException || e is System.Text.Json.JsonException)
        {
            entity.Api.Logger.Warning("SignFonts: rejected invalid formatting packet.");
        }
        return false;
    }

    private static string[] EntitySignText(BlockEntity entity) =>
        FindField(entity.GetType(), "textByCardinalDirection")?.GetValue(entity) is string[] directions ? directions.Select(t => t ?? "").ToArray()
        : new[] { FindField(entity.GetType(), "text")?.GetValue(entity) as string ?? "" };
}
