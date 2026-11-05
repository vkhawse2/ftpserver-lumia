using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Windows.Storage;

namespace FtpServer.Server
{
    // Virtual paths always start at "/" = the home folder. Every path is
    // normalised into segments and ".." can never pop above the root, so
    // nothing outside the home folder is reachable.
    public static class PathHelper
    {
        public static List<string> Normalize(string cwd, string arg)
        {
            var segs = new List<string>();
            string combined;
            if (arg != null && arg.StartsWith("/")) combined = arg;
            else combined = (cwd ?? "/") + "/" + (arg ?? "");
            foreach (var part in combined.Split('/'))
            {
                if (part.Length == 0 || part == ".") continue;
                if (part == "..")
                {
                    if (segs.Count > 0) segs.RemoveAt(segs.Count - 1);
                    continue;
                }
                segs.Add(part);
            }
            return segs;
        }

        public static string ToVirtual(List<string> segs)
        {
            return "/" + string.Join("/", segs.ToArray());
        }

        public static async Task<StorageFolder> GetFolderAsync(StorageFolder home, List<string> segs, bool create)
        {
            var cur = home;
            foreach (var seg in segs)
            {
                var next = await cur.TryGetItemAsync(seg) as StorageFolder;
                if (next == null)
                {
                    if (!create) return null;
                    next = await cur.CreateFolderAsync(seg, CreationCollisionOption.OpenIfExists);
                }
                cur = next;
            }
            return cur;
        }

        public static async Task<IStorageItem> GetItemAsync(StorageFolder home, List<string> segs)
        {
            if (segs.Count == 0) return home;
            var parent = await GetFolderAsync(home, segs.GetRange(0, segs.Count - 1), false);
            if (parent == null) return null;
            return await parent.TryGetItemAsync(segs[segs.Count - 1]);
        }

        // Folder that contains the item named by the last segment.
        public static async Task<StorageFolder> GetParentAsync(StorageFolder home, List<string> segs, bool create)
        {
            if (segs.Count == 0) return home;
            return await GetFolderAsync(home, segs.GetRange(0, segs.Count - 1), create);
        }
    }
}
