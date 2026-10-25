using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using Godot;

namespace Zhiji
{
    public static class Art
    {
        static readonly Dictionary<string, Texture2D> Cache = new Dictionary<string, Texture2D>();
        static string _dir;

        static string Find(string dir, int depth)
        {
            if (depth < 0 || dir == null) return null;
            try
            {
                if (Path.GetFileName(dir) == "art" && Directory.Exists(dir)) return dir;
                foreach (var sub in Directory.GetDirectories(dir))
                {
                    if (Path.GetFileName(sub) == "art") return sub;
                    var r = Find(sub, depth - 1);
                    if (r != null) return r;
                }
            }
            catch { }
            return null;
        }

        public static string Dir()
        {
            if (_dir != null) return _dir;
            try
            {
                string loc = typeof(Art).Assembly.Location;
                if (!string.IsNullOrEmpty(loc))
                {
                    var d = Path.Combine(Path.GetDirectoryName(loc), "art");
                    if (Directory.Exists(d)) { _dir = d; return _dir; }
                }
            }
            catch { }
            foreach (var root in new[]
            {
                "/data/data/com.megacrit.sts2re/files/mods",
                "/data/data/com.megacrit.sts2re/files/instances",
                "/data/data/com.megacrit.sts2re/files"
            })
            {
                try
                {
                    if (!Directory.Exists(root)) continue;
                    var d = Find(root, 4);
                    if (d != null) { _dir = d; ZLog.W("art dir " + d); return _dir; }
                }
                catch { }
            }
            ZLog.W("art dir NOT found");
            return null;
        }

        // 序列帧：art/<folder>/frame_0000.webp|.png
        public static Texture2D GetSequence(string folder, int index)
        {
            try
            {
                string dir = Path.Combine(Dir(), folder);
                if (index == 0) ZLog.W("seq dir " + dir + " exists=" + Directory.Exists(dir));
                foreach (var ext in new[] { ".webp", ".png" })
                {
                    string f = Path.Combine(dir, "frame_" + index.ToString("0000") + ext);
                    if (!File.Exists(f)) continue;
                    var img = new Image();
                    var err = ext == ".webp" ? img.LoadWebpFromBuffer(File.ReadAllBytes(f)) : img.LoadPngFromBuffer(File.ReadAllBytes(f));
                    if (err != Error.Ok) continue;
                    return ImageTexture.CreateFromImage(img);
                }
            }
            catch (Exception e) { ZLog.W("seq load fail " + e.Message); }
            return null;
        }

        public static Texture2D Get(string entry)
        {
            if (string.IsNullOrEmpty(entry)) return null;
            var key = entry.ToLowerInvariant();
            if (Cache.TryGetValue(key, out var t)) return t;
            Texture2D tex = null;
            try
            {
                string dir = Dir();
                if (dir != null && Directory.Exists(dir))
                {
                    foreach (var ext in new[] { ".png", ".jpg" })
                    {
                        string f = Path.Combine(dir, key + ext);
                        if (!File.Exists(f)) continue;
                        var img = new Image();
                        var err = img.LoadPngFromBuffer(File.ReadAllBytes(f));
                        if (err != Error.Ok) continue;
                        tex = ImageTexture.CreateFromImage(img);
                        break;
                    }
                }
            }
            catch (Exception e) { ZLog.W("art fail " + entry + " " + e.Message); }
            Cache[key] = tex;
            return tex;
        }
    }
}
