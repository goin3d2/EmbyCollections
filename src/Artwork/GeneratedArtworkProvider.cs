using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using EmbyCollections.Model;
using EmbyCollections.Sources;
using SkiaSharp;

namespace EmbyCollections.Artwork
{
    /// <summary>
    /// Draws a 1000x1500 poster: the collection title on a gradient (optionally under a TMDB network/company logo),
    /// or a mosaic of member posters with the title across the bottom.
    /// </summary>
    public sealed class GeneratedArtworkProvider : IArtworkProvider
    {
        public const int Width = 1000;
        public const int Height = 1500;

        private static readonly Lazy<SKTypeface> Font = new Lazy<SKTypeface>(() =>
        {
            using var stream = typeof(GeneratedArtworkProvider).Assembly.GetManifestResourceStream("EmbyCollections.TitleFont.ttf");
            if (stream == null)
            {
                return SKTypeface.FromFamilyName("sans-serif", SKFontStyle.Bold);
            }

            var copy = new MemoryStream();
            stream.CopyTo(copy);
            copy.Position = 0;
            return SKTypeface.FromStream(copy) ?? SKTypeface.FromFamilyName("sans-serif", SKFontStyle.Bold);
        });

        public ArtworkMode Mode => ArtworkMode.Generated;

        public async Task<ArtworkImage> GetAsync(ArtworkContext context, CancellationToken ct)
        {
            var c = context.Collection;
            var art = c.Artwork;
            if (art.Style == GeneratedStyle.Mosaic && context.MemberPosters.Count > 0)
            {
                var tiles = context.MemberPosters.Take(9).ToList();
                var cols = tiles.Count >= 9 ? 3 : tiles.Count >= 4 ? 2 : 1;
                tiles = tiles.Take(cols * cols).ToList();
                var key = "gen-mosaic|" + ArtworkHash(c, string.Join("|", tiles));
                return new ArtworkImage { Key = key, Source = "generated:mosaic", MimeType = "image/jpeg", Data = RenderMosaic(c.Name, tiles, cols, art) };
            }

            byte[] logo = null;
            var logoUrl = string.Empty;
            if (!string.IsNullOrWhiteSpace(art.LogoQuery))
            {
                var tmdb = new TmdbClient(context.Settings.TmdbApiKey);
                if (tmdb.IsConfigured)
                {
                    logoUrl = await tmdb.LogoUrlAsync(art.LogoQuery, ct).ConfigureAwait(false) ?? string.Empty;
                    if (logoUrl.Length > 0)
                    {
                        logo = (await ArtworkHttp.DownloadAsync(logoUrl, "tmdb-logo", ct).ConfigureAwait(false)).Data;
                    }
                }
            }

            return new ArtworkImage
            {
                Key = "gen-title|" + ArtworkHash(c, logoUrl),
                Source = logo != null ? "generated:logo" : "generated:title",
                MimeType = "image/jpeg",
                Data = RenderTitle(c.Name, logo, art),
            };
        }

        private static string ArtworkHash(CollectionDefinition c, string extra)
        {
            var a = c.Artwork;
            return ArtworkHttp.Hash($"{c.Name}|{a.Style}|{a.ColorTop}|{a.ColorBottom}|{a.LogoQuery}|{extra}|v1");
        }

        public static byte[] RenderTitle(string title, byte[] logo, ArtworkDefinition art)
        {
            using var surface = SKSurface.Create(new SKImageInfo(Width, Height));
            var canvas = surface.Canvas;
            DrawGradient(canvas, art);

            float textTop = Height * 0.56f;
            if (logo != null)
            {
                using var bmp = SKBitmap.Decode(logo);
                if (bmp != null)
                {
                    var box = new SKRect(Width * 0.14f, Height * 0.22f, Width * 0.86f, Height * 0.48f);
                    var scale = Math.Min(box.Width / bmp.Width, box.Height / bmp.Height);
                    var w = bmp.Width * scale;
                    var h = bmp.Height * scale;
                    var dest = SKRect.Create(box.MidX - (w / 2), box.MidY - (h / 2), w, h);
                    using var paint = new SKPaint { IsAntialias = true, FilterQuality = SKFilterQuality.High };
                    canvas.DrawBitmap(bmp, dest, paint);
                    textTop = Height * 0.62f;
                }
            }
            else
            {
                textTop = Height * 0.25f;
            }

            var textBottom = logo != null ? Height * 0.92f : Height * 0.80f;
            DrawTitle(canvas, title, new SKRect(Width * 0.08f, textTop, Width * 0.92f, textBottom), logo != null ? 96 : 130);
            return Encode(surface);
        }

        public static byte[] RenderMosaic(string title, IReadOnlyList<string> posterFiles, int cols, ArtworkDefinition art)
        {
            using var surface = SKSurface.Create(new SKImageInfo(Width, Height));
            var canvas = surface.Canvas;
            DrawGradient(canvas, art);
            var tileW = (float)Width / cols;
            var tileH = (float)Height / cols;
            using var paint = new SKPaint { IsAntialias = true, FilterQuality = SKFilterQuality.High };
            for (var i = 0; i < posterFiles.Count; i++)
            {
                using var bmp = TryDecode(posterFiles[i]);
                if (bmp == null)
                {
                    continue;
                }

                var cell = SKRect.Create((i % cols) * tileW, (i / cols) * tileH, tileW, tileH);
                var scale = Math.Max(cell.Width / bmp.Width, cell.Height / bmp.Height);
                var srcW = cell.Width / scale;
                var srcH = cell.Height / scale;
                var src = SKRect.Create((bmp.Width - srcW) / 2, (bmp.Height - srcH) / 2, srcW, srcH);
                canvas.DrawBitmap(bmp, src, cell, paint);
            }

            // Darken the bottom third so the title reads.
            using (var shade = new SKPaint())
            {
                shade.Shader = SKShader.CreateLinearGradient(
                    new SKPoint(0, Height * 0.55f), new SKPoint(0, Height),
                    new[] { SKColors.Transparent, new SKColor(0, 0, 0, 235) }, null, SKShaderTileMode.Clamp);
                canvas.DrawRect(0, Height * 0.55f, Width, Height * 0.45f, shade);
            }

            DrawTitle(canvas, title, new SKRect(Width * 0.06f, Height * 0.74f, Width * 0.94f, Height * 0.96f), 110);
            return Encode(surface);
        }

        private static void DrawGradient(SKCanvas canvas, ArtworkDefinition art)
        {
            var top = SKColor.TryParse(art.ColorTop, out var t) ? t : new SKColor(0x1f, 0x2a, 0x44);
            var bottom = SKColor.TryParse(art.ColorBottom, out var b) ? b : new SKColor(0x0b, 0x0f, 0x19);
            using var paint = new SKPaint
            {
                Shader = SKShader.CreateLinearGradient(new SKPoint(0, 0), new SKPoint(0, Height), new[] { top, bottom }, null, SKShaderTileMode.Clamp),
            };
            canvas.DrawRect(0, 0, Width, Height, paint);
        }

        /// <summary>Upper-cased title, wrapped into the box, shrunk until it fits (max 4 lines).</summary>
        private static void DrawTitle(SKCanvas canvas, string title, SKRect box, float startSize)
        {
            var text = (title ?? string.Empty).Trim().ToUpperInvariant();
            using var paint = new SKPaint { Typeface = Font.Value, IsAntialias = true, Color = SKColors.White, TextAlign = SKTextAlign.Center };
            List<string> lines;
            var size = startSize;
            while (true)
            {
                paint.TextSize = size;
                lines = Wrap(text, paint, box.Width);
                var height = lines.Count * size * 1.1f;
                if ((height <= box.Height && lines.Count <= 4 && lines.All(l => paint.MeasureText(l) <= box.Width)) || size <= 28)
                {
                    break;
                }

                size *= 0.92f;
            }

            var lineH = size * 1.1f;
            var y = box.Top + ((box.Height - (lines.Count * lineH)) / 2) + size;
            using var shadow = new SKPaint
            {
                Typeface = paint.Typeface, IsAntialias = true, TextAlign = SKTextAlign.Center, TextSize = size,
                Color = new SKColor(0, 0, 0, 160), MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, size * 0.06f),
            };
            foreach (var line in lines)
            {
                canvas.DrawText(line, box.MidX + 3, y + 4, shadow);
                canvas.DrawText(line, box.MidX, y, paint);
                y += lineH;
            }
        }

        private static List<string> Wrap(string text, SKPaint paint, float width)
        {
            var lines = new List<string>();
            var current = string.Empty;
            foreach (var word in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                var trial = current.Length == 0 ? word : current + " " + word;
                if (paint.MeasureText(trial) <= width || current.Length == 0)
                {
                    current = trial;
                }
                else
                {
                    lines.Add(current);
                    current = word;
                }
            }

            if (current.Length > 0)
            {
                lines.Add(current);
            }

            return lines;
        }

        private static SKBitmap TryDecode(string path)
        {
            try
            {
                return File.Exists(path) ? SKBitmap.Decode(path) : null;
            }
            catch
            {
                return null;
            }
        }

        private static byte[] Encode(SKSurface surface)
        {
            using var image = surface.Snapshot();
            using var data = image.Encode(SKEncodedImageFormat.Jpeg, 90);
            return data.ToArray();
        }
    }
}
