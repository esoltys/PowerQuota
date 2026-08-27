using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.IO;
using Xunit;

namespace PowerQuota.Core.Tests;

public class AssetGeneratorTests
{
    [Fact]
    public void GenerateAndValidateAppAssets()
    {
        // Find repository Assets directory
        string baseDir = AppContext.BaseDirectory;
        string? repoRoot = null;
        var dir = new DirectoryInfo(baseDir);
        while (dir != null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "PowerQuota.sln")))
            {
                repoRoot = dir.FullName;
                break;
            }
            dir = dir.Parent;
        }

        Assert.NotNull(repoRoot);
        string assetsDir = Path.Combine(repoRoot!, "src", "PowerQuota.CommandPalette", "Assets");
        string iconsDir = Path.Combine(assetsDir, "Icons");

        Directory.CreateDirectory(assetsDir);
        Directory.CreateDirectory(iconsDir);

        // 1. Icons/powerquota.png
        RenderIcon(Path.Combine(iconsDir, "powerquota.png"), 512, 512, withBg: true);

        // 2. Square44x44Logo
        RenderIcon(Path.Combine(assetsDir, "Square44x44Logo.png"), 44, 44, withBg: true);
        RenderIcon(Path.Combine(assetsDir, "Square44x44Logo.targetsize-16.png"), 16, 16, withBg: true);
        RenderIcon(Path.Combine(assetsDir, "Square44x44Logo.targetsize-24.png"), 24, 24, withBg: true);
        RenderIcon(Path.Combine(assetsDir, "Square44x44Logo.targetsize-32.png"), 32, 32, withBg: true);
        RenderIcon(Path.Combine(assetsDir, "Square44x44Logo.targetsize-44.png"), 44, 44, withBg: true);
        RenderIcon(Path.Combine(assetsDir, "Square44x44Logo.targetsize-48.png"), 48, 48, withBg: true);
        RenderIcon(Path.Combine(assetsDir, "Square44x44Logo.targetsize-256.png"), 256, 256, withBg: true);

        RenderIcon(Path.Combine(assetsDir, "Square44x44Logo.altform-unplated_targetsize-16.png"), 16, 16, withBg: false);
        RenderIcon(Path.Combine(assetsDir, "Square44x44Logo.altform-unplated_targetsize-24.png"), 24, 24, withBg: false);
        RenderIcon(Path.Combine(assetsDir, "Square44x44Logo.altform-unplated_targetsize-32.png"), 32, 32, withBg: false);
        RenderIcon(Path.Combine(assetsDir, "Square44x44Logo.altform-unplated_targetsize-44.png"), 44, 44, withBg: false);
        RenderIcon(Path.Combine(assetsDir, "Square44x44Logo.altform-unplated_targetsize-48.png"), 48, 48, withBg: false);
        RenderIcon(Path.Combine(assetsDir, "Square44x44Logo.altform-unplated_targetsize-256.png"), 256, 256, withBg: false);

        // 3. Square150x150Logo
        RenderIcon(Path.Combine(assetsDir, "Square150x150Logo.png"), 150, 150, withBg: true);
        RenderIcon(Path.Combine(assetsDir, "Square150x150Logo.scale-100.png"), 150, 150, withBg: true);
        RenderIcon(Path.Combine(assetsDir, "Square150x150Logo.scale-125.png"), 188, 188, withBg: true);
        RenderIcon(Path.Combine(assetsDir, "Square150x150Logo.scale-150.png"), 225, 225, withBg: true);
        RenderIcon(Path.Combine(assetsDir, "Square150x150Logo.scale-200.png"), 300, 300, withBg: true);
        RenderIcon(Path.Combine(assetsDir, "Square150x150Logo.scale-400.png"), 600, 600, withBg: true);

        // 4. StoreLogo
        RenderIcon(Path.Combine(assetsDir, "StoreLogo.png"), 50, 50, withBg: true);
        RenderIcon(Path.Combine(assetsDir, "StoreLogo.scale-100.png"), 50, 50, withBg: true);
        RenderIcon(Path.Combine(assetsDir, "StoreLogo.scale-125.png"), 63, 63, withBg: true);
        RenderIcon(Path.Combine(assetsDir, "StoreLogo.scale-150.png"), 75, 75, withBg: true);
        RenderIcon(Path.Combine(assetsDir, "StoreLogo.scale-200.png"), 100, 100, withBg: true);
        RenderIcon(Path.Combine(assetsDir, "StoreLogo.scale-400.png"), 200, 200, withBg: true);

        // 5. Wide310x150Logo
        RenderWideTile(Path.Combine(assetsDir, "Wide310x150Logo.png"), 310, 150);
        RenderWideTile(Path.Combine(assetsDir, "Wide310x150Logo.scale-100.png"), 310, 150);
        RenderWideTile(Path.Combine(assetsDir, "Wide310x150Logo.scale-125.png"), 388, 188);
        RenderWideTile(Path.Combine(assetsDir, "Wide310x150Logo.scale-150.png"), 465, 225);
        RenderWideTile(Path.Combine(assetsDir, "Wide310x150Logo.scale-200.png"), 620, 300);
        RenderWideTile(Path.Combine(assetsDir, "Wide310x150Logo.scale-400.png"), 1240, 600);

        // 6. SplashScreen
        RenderSplash(Path.Combine(assetsDir, "SplashScreen.png"), 620, 300);
        RenderSplash(Path.Combine(assetsDir, "SplashScreen.scale-100.png"), 620, 300);
        RenderSplash(Path.Combine(assetsDir, "SplashScreen.scale-125.png"), 775, 375);
        RenderSplash(Path.Combine(assetsDir, "SplashScreen.scale-150.png"), 930, 450);
        RenderSplash(Path.Combine(assetsDir, "SplashScreen.scale-200.png"), 1240, 600);
        RenderSplash(Path.Combine(assetsDir, "SplashScreen.scale-400.png"), 2480, 1200);

        // 7. SmallTile & LargeTile (Store tiles)
        RenderIcon(Path.Combine(assetsDir, "SmallTile.png"), 71, 71, withBg: true);
        RenderIcon(Path.Combine(assetsDir, "SmallTile.scale-100.png"), 71, 71, withBg: true);
        RenderIcon(Path.Combine(assetsDir, "SmallTile.scale-200.png"), 142, 142, withBg: true);
        RenderIcon(Path.Combine(assetsDir, "LargeTile.png"), 310, 310, withBg: true);
        RenderIcon(Path.Combine(assetsDir, "LargeTile.scale-100.png"), 310, 310, withBg: true);
        RenderIcon(Path.Combine(assetsDir, "LargeTile.scale-200.png"), 620, 620, withBg: true);

        // Assert all critical files exist and have non-zero length
        Assert.True(File.Exists(Path.Combine(assetsDir, "Square150x150Logo.png")));
        Assert.True(File.Exists(Path.Combine(assetsDir, "Square44x44Logo.png")));
        Assert.True(File.Exists(Path.Combine(assetsDir, "StoreLogo.png")));
        Assert.True(File.Exists(Path.Combine(assetsDir, "Wide310x150Logo.png")));
        Assert.True(File.Exists(Path.Combine(assetsDir, "SplashScreen.png")));
        Assert.True(File.Exists(Path.Combine(assetsDir, "SmallTile.png")));
        Assert.True(File.Exists(Path.Combine(assetsDir, "LargeTile.png")));
        Assert.True(File.Exists(Path.Combine(iconsDir, "powerquota.png")));
    }

    private static void RenderIcon(string path, int width, int height, bool withBg)
    {
        using var bmp = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);

        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.Clear(Color.Transparent);

        float margin = width * 0.06f;
        float drawW = width - (margin * 2);
        float drawH = height - (margin * 2);

        if (withBg)
        {
            float cornerRadius = width * 0.22f;
            using var bgPath = CreateRoundedRect(margin, margin, drawW, drawH, cornerRadius);
            using var bgBrush = new LinearGradientBrush(
                new PointF(0, margin),
                new PointF(0, margin + drawH),
                Color.FromArgb(255, 30, 37, 54),
                Color.FromArgb(255, 14, 17, 24));
            g.FillPath(bgBrush, bgPath);

            using var borderPen = new Pen(Color.FromArgb(90, 59, 130, 246), Math.Max(1.0f, width * 0.015f));
            g.DrawPath(borderPen, bgPath);
        }

        float cx = width / 2.0f;
        float cy = height / 2.0f;
        float radius = width * (withBg ? 0.30f : 0.40f);
        float strokeWidth = Math.Max(2.0f, radius * 0.22f);

        // Track Arc (background)
        using (var trackPen = new Pen(Color.FromArgb(120, 30, 41, 59), strokeWidth))
        {
            trackPen.StartCap = LineCap.Round;
            trackPen.EndCap = LineCap.Round;
            g.DrawArc(trackPen, cx - radius, cy - radius, radius * 2, radius * 2, 135, 270);
        }

        // Active Arc (Cyan glow)
        using (var activePen = new Pen(Color.FromArgb(255, 0, 229, 255), strokeWidth))
        {
            activePen.StartCap = LineCap.Round;
            activePen.EndCap = LineCap.Round;
            g.DrawArc(activePen, cx - radius, cy - radius, radius * 2, radius * 2, 135, 200);
        }

        // Glow accent dot
        float endAngleRad = (float)((135 + 200) * Math.PI / 180.0);
        float dotX = cx + (float)(Math.Cos(endAngleRad) * radius);
        float dotY = cy + (float)(Math.Sin(endAngleRad) * radius);
        float dotR = Math.Max(2.0f, strokeWidth * 0.45f);
        using (var dotBrush = new SolidBrush(Color.White))
        {
            g.FillEllipse(dotBrush, dotX - dotR, dotY - dotR, dotR * 2, dotR * 2);
        }

        // Center Bolt / AI Symbol
        float boltScale = radius * 0.95f;
        var boltPoints = new PointF[]
        {
            new PointF(cx + boltScale * 0.12f, cy - boltScale * 0.65f),
            new PointF(cx - boltScale * 0.35f, cy + boltScale * 0.05f),
            new PointF(cx - boltScale * 0.02f, cy + boltScale * 0.05f),
            new PointF(cx - boltScale * 0.15f, cy + boltScale * 0.68f),
            new PointF(cx + boltScale * 0.40f, cy - boltScale * 0.05f),
            new PointF(cx + boltScale * 0.05f, cy - boltScale * 0.05f),
        };

        using (var boltBrush = new LinearGradientBrush(
            new PointF(cx, cy - boltScale),
            new PointF(cx, cy + boltScale),
            Color.FromArgb(255, 255, 255, 255),
            Color.FromArgb(255, 6, 182, 212)))
        {
            g.FillPolygon(boltBrush, boltPoints);
        }

        bmp.Save(path, ImageFormat.Png);
    }

    private static void RenderWideTile(string path, int width, int height)
    {
        using var bmp = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);

        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;

        using var bgBrush = new LinearGradientBrush(
            new PointF(0, 0),
            new PointF(width, height),
            Color.FromArgb(255, 22, 27, 39),
            Color.FromArgb(255, 11, 14, 20));
        g.FillRectangle(bgBrush, 0, 0, width, height);

        float iconSize = height * 0.58f;
        float iconX = width * 0.07f;
        float iconY = (height - iconSize) / 2.0f;

        using var tempBmp = new Bitmap((int)iconSize, (int)iconSize, PixelFormat.Format32bppArgb);
        using (var tempG = Graphics.FromImage(tempBmp))
        {
            tempG.SmoothingMode = SmoothingMode.AntiAlias;
            tempG.Clear(Color.Transparent);
            RenderIconToGraphics(tempG, (int)iconSize, (int)iconSize, withBg: true);
        }
        g.DrawImage(tempBmp, iconX, iconY, iconSize, iconSize);

        float textX = iconX + iconSize + (width * 0.05f);
        float titleFontSize = height * 0.17f;
        float subFontSize = height * 0.09f;

        using var titleFont = new Font("Segoe UI", titleFontSize, FontStyle.Bold, GraphicsUnit.Pixel);
        using var subFont = new Font("Segoe UI", subFontSize, FontStyle.Regular, GraphicsUnit.Pixel);
        using var titleBrush = new SolidBrush(Color.White);
        using var subBrush = new SolidBrush(Color.FromArgb(255, 148, 163, 184));

        g.DrawString("PowerQuota", titleFont, titleBrush, textX, height * 0.28f);
        g.DrawString("AI Coding Quota Tracker", subFont, subBrush, textX, height * 0.56f);

        bmp.Save(path, ImageFormat.Png);
    }

    private static void RenderSplash(string path, int width, int height)
    {
        using var bmp = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);

        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;

        using var bgBrush = new LinearGradientBrush(
            new PointF(0, 0),
            new PointF(0, height),
            Color.FromArgb(255, 15, 19, 28),
            Color.FromArgb(255, 9, 11, 16));
        g.FillRectangle(bgBrush, 0, 0, width, height);

        float iconSize = height * 0.42f;
        float iconX = (width - iconSize) / 2.0f;
        float iconY = height * 0.20f;

        using var tempBmp = new Bitmap((int)iconSize, (int)iconSize, PixelFormat.Format32bppArgb);
        using (var tempG = Graphics.FromImage(tempBmp))
        {
            tempG.SmoothingMode = SmoothingMode.AntiAlias;
            tempG.Clear(Color.Transparent);
            RenderIconToGraphics(tempG, (int)iconSize, (int)iconSize, withBg: true);
        }
        g.DrawImage(tempBmp, iconX, iconY, iconSize, iconSize);

        float titleFontSize = height * 0.09f;
        float subFontSize = height * 0.048f;
        using var titleFont = new Font("Segoe UI", titleFontSize, FontStyle.Bold, GraphicsUnit.Pixel);
        using var subFont = new Font("Segoe UI", subFontSize, FontStyle.Regular, GraphicsUnit.Pixel);
        using var titleBrush = new SolidBrush(Color.White);
        using var subBrush = new SolidBrush(Color.FromArgb(255, 148, 163, 184));

        var sf = new StringFormat { Alignment = StringAlignment.Center };
        g.DrawString("PowerQuota", titleFont, titleBrush, width / 2.0f, height * 0.67f, sf);
        g.DrawString("AI Coding Quota Monitor for PowerToys", subFont, subBrush, width / 2.0f, height * 0.79f, sf);

        bmp.Save(path, ImageFormat.Png);
    }

    private static void RenderIconToGraphics(Graphics g, int width, int height, bool withBg)
    {
        float margin = width * 0.06f;
        float drawW = width - (margin * 2);
        float drawH = height - (margin * 2);

        if (withBg)
        {
            float cornerRadius = width * 0.22f;
            using var bgPath = CreateRoundedRect(margin, margin, drawW, drawH, cornerRadius);
            using var bgBrush = new LinearGradientBrush(
                new PointF(0, margin),
                new PointF(0, margin + drawH),
                Color.FromArgb(255, 30, 37, 54),
                Color.FromArgb(255, 14, 17, 24));
            g.FillPath(bgBrush, bgPath);

            using var borderPen = new Pen(Color.FromArgb(90, 59, 130, 246), Math.Max(1.0f, width * 0.015f));
            g.DrawPath(borderPen, bgPath);
        }

        float cx = width / 2.0f;
        float cy = height / 2.0f;
        float radius = width * (withBg ? 0.30f : 0.40f);
        float strokeWidth = Math.Max(2.0f, radius * 0.22f);

        using (var trackPen = new Pen(Color.FromArgb(120, 30, 41, 59), strokeWidth))
        {
            trackPen.StartCap = LineCap.Round;
            trackPen.EndCap = LineCap.Round;
            g.DrawArc(trackPen, cx - radius, cy - radius, radius * 2, radius * 2, 135, 270);
        }

        using (var activePen = new Pen(Color.FromArgb(255, 0, 229, 255), strokeWidth))
        {
            activePen.StartCap = LineCap.Round;
            activePen.EndCap = LineCap.Round;
            g.DrawArc(activePen, cx - radius, cy - radius, radius * 2, radius * 2, 135, 200);
        }

        float endAngleRad = (float)((135 + 200) * Math.PI / 180.0);
        float dotX = cx + (float)(Math.Cos(endAngleRad) * radius);
        float dotY = cy + (float)(Math.Sin(endAngleRad) * radius);
        float dotR = Math.Max(2.0f, strokeWidth * 0.45f);
        using (var dotBrush = new SolidBrush(Color.White))
        {
            g.FillEllipse(dotBrush, dotX - dotR, dotY - dotR, dotR * 2, dotR * 2);
        }

        float boltScale = radius * 0.95f;
        var boltPoints = new PointF[]
        {
            new PointF(cx + boltScale * 0.12f, cy - boltScale * 0.65f),
            new PointF(cx - boltScale * 0.35f, cy + boltScale * 0.05f),
            new PointF(cx - boltScale * 0.02f, cy + boltScale * 0.05f),
            new PointF(cx - boltScale * 0.15f, cy + boltScale * 0.68f),
            new PointF(cx + boltScale * 0.40f, cy - boltScale * 0.05f),
            new PointF(cx + boltScale * 0.05f, cy - boltScale * 0.05f),
        };

        using (var boltBrush = new LinearGradientBrush(
            new PointF(cx, cy - boltScale),
            new PointF(cx, cy + boltScale),
            Color.FromArgb(255, 255, 255, 255),
            Color.FromArgb(255, 6, 182, 212)))
        {
            g.FillPolygon(boltBrush, boltPoints);
        }
    }

    private static GraphicsPath CreateRoundedRect(float x, float y, float width, float height, float radius)
    {
        var path = new GraphicsPath();
        float diameter = radius * 2;
        path.AddArc(x, y, diameter, diameter, 180, 90);
        path.AddArc(x + width - diameter, y, diameter, diameter, 270, 90);
        path.AddArc(x + width - diameter, y + height - diameter, diameter, diameter, 0, 90);
        path.AddArc(x, y + height - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }
}
