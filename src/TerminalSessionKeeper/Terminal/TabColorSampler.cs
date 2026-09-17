using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using TerminalSessionKeeper.Logging;

namespace TerminalSessionKeeper.Terminal;

/// <summary>
/// One window's worth of tab colours. <paramref name="Read"/> is false when the window could not
/// be read at all — minimized, unrenderable, or a frame the sampler would not vouch for — and
/// every entry in <paramref name="Colors"/> is then silence rather than "this tab has no colour".
/// </summary>
/// <param name="Read">True when the frame was good enough to draw conclusions from.</param>
/// <param name="Colors">Index for index with the window's tabs; null where a tab has no colour.</param>
public sealed record TabColorScan(bool Read, IReadOnlyList<string?> Colors);

/// <summary>
/// Recovers each tab's colour by rendering the terminal window and reading the pixels.
///
/// No Windows Terminal API exposes a tab colour, and nothing persists one: a colour set from the
/// tab's context menu lives only in the running window. But UI Automation gives every tab a
/// bounding rectangle, and the pixels inside it are the colour — so the colour is readable after
/// all.
///
/// The window is rendered with <c>PrintWindow</c> rather than captured off the screen, which
/// matters more than it sounds: snapshots run on a timer, so the terminal is usually behind a
/// browser or an editor when one fires. Asking the window to draw itself reads the right pixels
/// no matter what is stacked on top of it. A minimized window has nothing to draw and is skipped.
///
/// Windows Terminal renders an unfocused coloured tab at 30% of its colour over the tab-strip
/// background — measured against tabs opened with four known colours, where crimson #DC143C read
/// back as #622632 — so the original is recovered by undoing that blend. The background is taken
/// from the uncoloured tabs on screen rather than hard-coded, so a themed terminal works too, and
/// the focused tab is drawn at full strength and needs no correction at all.
///
/// Everything here fails closed: anything uncertain is reported as "no colour" rather than as a
/// guess, because a wrong colour would be written into the snapshot and reapplied on restore.
///
/// Failing closed has to mean "no evidence", not "no colour". A restored colour is sampled again
/// on the next snapshot, so a reading mistaken for a colour — or for the absence of one — is fed
/// back in and re-read, and the un-blending divides by 0.30 and multiplies every rounding error
/// by more than three. That loop is what once walked a tab from #DD153D to #FF0051 and another
/// from #010101 to black. <see cref="TabColorScan.Read"/> is how a window that could not be read
/// is told apart from one where nothing is coloured; only the second is evidence.
/// </summary>
public sealed class TabColorSampler
{
    /// <summary>
    /// How much of its colour an unfocused tab shows over the strip background. Measured, not
    /// assumed: four known colours all read back at 0.29–0.31.
    /// </summary>
    public const double UnfocusedBlend = 0.30;

    /// <summary>Channel distance below which two colours are the same colour.</summary>
    private const int SameColorTolerance = 6;

    /// <summary>The close button lives on the right of a tab and is not part of its colour.</summary>
    private const int RightInset = 26;

    private const int HorizontalInset = 6;
    private const int VerticalInset = 2;

    /// <summary>Windows parks a minimized window here, where it has no content to render.</summary>
    private const int MinimizedEdge = -30000;

    private const uint RenderFullContent = 2;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool PrintWindow(IntPtr window, IntPtr deviceContext, uint flags);

    private readonly ILog _log;

    public TabColorSampler(ILog log) => _log = log;

    /// <summary>
    /// Colours for the window's tabs, index for index with <see cref="TerminalWindow.Tabs"/>. An
    /// entry is null when the tab has no colour, or when its colour could not be read with
    /// confidence.
    /// </summary>
    public TabColorScan Sample(TerminalWindow window)
    {
        var none = new TabColorScan(false, new string?[window.Tabs.Count]);
        if (window.Tabs.Count == 0) return none;

        if (window.Handle == IntPtr.Zero)
        {
            _log.Debug("Tab colours skipped: the window has no handle to render.");
            return none;
        }

        if (window.Bounds.Width < 200 || window.Bounds.Height < 100 || window.Bounds.X <= MinimizedEdge)
        {
            _log.Debug("Tab colours skipped: the terminal window is minimized.");
            return none;
        }

        try
        {
            using var rendered = Render(window);
            if (rendered is null) return none;

            using var pixels = new LockedBitmap(rendered);

            // Sample every tab first; what a reading means depends on the others.
            var readings = new Color?[window.Tabs.Count];
            for (var index = 0; index < window.Tabs.Count; index++)
            {
                readings[index] = ModalColor(pixels, window.Bounds, window.Tabs[index].Bounds,
                    HorizontalInset, RightInset, VerticalInset);
            }

            var background = Background(EmptyStripColor(pixels, window),
                StripBackground(window.Tabs, readings), out var disagreement);

            if (background is null)
            {
                _log.Debug($"Tab colours skipped: {disagreement}");
                return none;
            }

            var terminalBackground = TerminalBackground(pixels, window.Bounds);

            var colors = new string?[window.Tabs.Count];
            var found = 0;

            for (var index = 0; index < window.Tabs.Count; index++)
            {
                colors[index] = Interpret(readings[index], window.Tabs[index].IsSelected,
                    background.Value, terminalBackground);

                if (colors[index] is not null) found++;
            }

            _log.Debug($"Tab colours: {found} of {window.Tabs.Count} tab(s) are coloured " +
                       $"(strip background {ToHex(background.Value)}).");

            return new TabColorScan(true, colors);
        }
        catch (Exception ex) when (ex is ExternalException or InvalidOperationException
                                      or ArgumentException or OutOfMemoryException)
        {
            _log.Debug($"Tab colours could not be sampled. {ex.Message}");
            return none;
        }
    }

    /// <summary>
    /// Asks the window to draw itself into a bitmap. PW_RENDERFULLCONTENT is what makes this work
    /// for a hardware-composed window like Windows Terminal; without it the result is blank.
    /// </summary>
    private Bitmap? Render(TerminalWindow window)
    {
        Bitmap? bitmap = null;

        try
        {
            bitmap = new Bitmap(window.Bounds.Width, window.Bounds.Height, PixelFormat.Format32bppArgb);

            using (var graphics = Graphics.FromImage(bitmap))
            {
                var deviceContext = graphics.GetHdc();

                try
                {
                    if (!PrintWindow(window.Handle, deviceContext, RenderFullContent))
                    {
                        _log.Debug("Tab colours skipped: the window declined to render itself.");
                        bitmap.Dispose();
                        return null;
                    }
                }
                finally
                {
                    graphics.ReleaseHdc(deviceContext);
                }
            }

            return bitmap;
        }
        catch (Exception ex) when (ex is ExternalException or ArgumentException or OutOfMemoryException)
        {
            bitmap?.Dispose();
            _log.Debug($"Tab colours skipped: the window could not be rendered. {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Turns one reading into a colour, or into nothing at all.
    ///
    /// An unfocused tab shows a fraction of its colour over the strip background, so a reading
    /// equal to that background means the tab has no colour, and anything else is un-blended back
    /// to the original. The focused tab shows its colour at full strength, but an uncoloured one
    /// shows the terminal's own background — indistinguishable from a tab deliberately coloured to
    /// match it, so that case is left for the previous snapshot to answer.
    /// </summary>
    public static string? Interpret(Color? reading, bool isSelected, Color stripBackground,
        Color? terminalBackground)
    {
        if (reading is not { } color) return null;

        if (!isSelected)
        {
            if (IsSameColor(color, stripBackground)) return null;
            return ToHex(Unblend(color, stripBackground, UnfocusedBlend));
        }

        if (terminalBackground is null || IsSameColor(color, terminalBackground.Value)) return null;

        // The strip background showing through a selected tab means no colour either.
        return IsSameColor(color, stripBackground) ? null : ToHex(color);
    }

    /// <summary>
    /// The strip background the rest of the reading is measured against, or null when the two
    /// ways of finding it disagree.
    ///
    /// Everything hangs off this one colour: an unfocused tab is called uncoloured because it
    /// matches the background, and coloured tabs are un-blended out of it. A background read
    /// wrong by more than the tolerance therefore does not spoil one tab, it invents a colour
    /// for every uncoloured tab in the window at once — which is exactly what a frame that came
    /// back black once did, turning a plain tab into #AAAAAA. So when the bare strip and the
    /// tabs themselves both have something to say and they disagree, neither is believed.
    /// </summary>
    public static Color? Background(Color? fromEmptyStrip, Color? fromTabs, out string disagreement)
    {
        disagreement = string.Empty;

        if (fromEmptyStrip is { } strip && fromTabs is { } tabs && !IsSameColor(strip, tabs))
        {
            disagreement = $"the bare tab strip reads {ToHex(strip)} but the tabs themselves " +
                           $"read {ToHex(tabs)}, so the background is not settled.";
            return null;
        }

        var background = fromEmptyStrip ?? fromTabs;
        if (background is null) disagreement = "the tab strip background could not be identified.";

        return background;
    }

    /// <summary>
    /// The bare tab strip, to the right of the last tab and the new-tab button but left of the
    /// window buttons. This is the reliable reading: it is the background itself rather than an
    /// inference from the tabs, so it holds even when every open tab happens to be coloured.
    /// </summary>
    private static Color? EmptyStripColor(LockedBitmap pixels, TerminalWindow window)
    {
        if (window.Tabs.Count == 0) return null;

        var row = window.Tabs[0].Bounds;
        if (row.Height < 8) return null;

        // Past the "+" button after the last tab, and well clear of minimise/maximise/close.
        var left = window.Tabs.Max(tab => tab.Bounds.Right) + 60;
        var right = window.Bounds.Right - 170;
        if (right - left < 24) return null;

        return ModalColor(pixels, window.Bounds,
            new Rectangle(left, row.Y, right - left, row.Height), 0, 0, VerticalInset);
    }

    /// <summary>
    /// The fallback when the strip is full: uncoloured tabs are the common case, so a reading
    /// shared by two or more unfocused tabs is the background. One tab is never enough — it might
    /// itself be the coloured one, and mistaking a colour for the background would strip the
    /// colour from every tab that actually has one.
    /// </summary>
    public static Color? StripBackground(IReadOnlyList<TabElement> tabs, IReadOnlyList<Color?> readings)
    {
        var counts = new Dictionary<int, (Color Color, int Count)>();

        for (var index = 0; index < tabs.Count; index++)
        {
            if (tabs[index].IsSelected || readings[index] is not { } color) continue;

            var key = color.ToArgb();
            counts[key] = counts.TryGetValue(key, out var existing)
                ? (color, existing.Count + 1)
                : (color, 1);
        }

        if (counts.Count == 0) return null;

        var best = counts.Values.OrderByDescending(entry => entry.Count).First();
        return best.Count >= 2 ? best.Color : null;
    }

    /// <summary>
    /// The terminal's own background, read from a patch low in the window where text is least
    /// likely. Used only to recognise an uncoloured focused tab.
    /// </summary>
    private static Color? TerminalBackground(LockedBitmap pixels, Rectangle window)
    {
        if (window.Width < 80 || window.Height < 120) return null;

        // Window-relative, bottom-right quarter: past the prompt on most screens.
        var patch = new Rectangle(
            window.Width / 2,
            window.Height - window.Height / 6,
            window.Width / 3,
            window.Height / 12);

        return ModalColor(pixels, new Rectangle(0, 0, window.Width, window.Height),
            new Rectangle(patch.X + window.X, patch.Y + window.Y, patch.Width, patch.Height),
            4, 4, 2);
    }

    /// <summary>
    /// The most common colour inside a rectangle given in screen coordinates, translated into the
    /// rendered window's own. Null when the region is not a flat enough fill to judge.
    /// </summary>
    private static Color? ModalColor(LockedBitmap pixels, Rectangle window, Rectangle bounds,
        int leftInset, int rightInset, int verticalInset)
    {
        if (bounds.Width <= leftInset + rightInset || bounds.Height <= verticalInset * 2) return null;

        // PrintWindow draws from the window's own top-left, so screen coordinates shift by it.
        var left = bounds.X - window.X + leftInset;
        var right = bounds.Right - window.X - rightInset;
        var top = bounds.Y - window.Y + verticalInset;
        var bottom = bounds.Bottom - window.Y - verticalInset;

        var counts = new Dictionary<int, int>();
        var sampled = 0;

        for (var y = top; y < bottom; y++)
        {
            for (var x = left; x < right; x++)
            {
                if (!pixels.TryGetPixel(x, y, out var argb)) continue;

                counts[argb] = counts.TryGetValue(argb, out var count) ? count + 1 : 1;
                sampled++;
            }
        }

        if (sampled < 40) return null;

        // A tab scrolled out of an overflowing strip still has a bounding rectangle, and it can
        // sit almost entirely outside the window: what is left inside is a sliver of the window
        // edge that reads as a perfectly flat colour and is not the tab at all. Only a rectangle
        // that is mostly on screen is a reading.
        var area = Math.Max(right - left, 0) * Math.Max(bottom - top, 0);
        if (sampled * 4 < area * 3) return null;

        var best = counts.OrderByDescending(entry => entry.Value).First();

        // Text and icons cover a minority of a tab; less than a clear majority means the region is
        // not a flat fill and the reading cannot be trusted.
        return best.Value * 2 >= sampled ? Color.FromArgb(best.Key) : null;
    }

    /// <summary>
    /// Recovers the colour behind a blend: the tab shows
    /// <c>alpha * colour + (1 - alpha) * background</c>, so the colour is the reading pushed back
    /// out from the background by the same factor.
    /// </summary>
    public static Color Unblend(Color blended, Color background, double alpha)
    {
        static int Channel(int blended, int background, double alpha) =>
            Math.Clamp((int)Math.Round(background + (blended - background) / alpha), 0, 255);

        return Color.FromArgb(
            Channel(blended.R, background.R, alpha),
            Channel(blended.G, background.G, alpha),
            Channel(blended.B, background.B, alpha));
    }

    public static bool IsSameColor(Color left, Color right) =>
        Math.Abs(left.R - right.R) <= SameColorTolerance
        && Math.Abs(left.G - right.G) <= SameColorTolerance
        && Math.Abs(left.B - right.B) <= SameColorTolerance;

    public static string ToHex(Color color) => $"#{color.R:X2}{color.G:X2}{color.B:X2}";

    /// <summary>
    /// Direct access to a rendered bitmap's pixels. GetPixel per call would mean millions of
    /// bounds checks and marshalling hops across a tab strip.
    /// </summary>
    private sealed class LockedBitmap : IDisposable
    {
        private readonly Bitmap _bitmap;
        private readonly BitmapData _data;
        private readonly int[] _pixels;

        public LockedBitmap(Bitmap bitmap)
        {
            _bitmap = bitmap;
            _data = bitmap.LockBits(new Rectangle(0, 0, bitmap.Width, bitmap.Height),
                ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);

            _pixels = new int[bitmap.Width * bitmap.Height];
            Marshal.Copy(_data.Scan0, _pixels, 0, _pixels.Length);
        }

        public bool TryGetPixel(int x, int y, out int argb)
        {
            argb = 0;
            if (x < 0 || y < 0 || x >= _bitmap.Width || y >= _bitmap.Height) return false;

            // Opaque: a rendered window carries no meaningful alpha, and a stray one would break
            // the equality comparisons.
            argb = _pixels[y * _bitmap.Width + x] | unchecked((int)0xFF000000);
            return true;
        }

        public void Dispose() => _bitmap.UnlockBits(_data);
    }
}
