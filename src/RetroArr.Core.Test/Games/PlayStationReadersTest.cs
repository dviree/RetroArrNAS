using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using NUnit.Framework;
using RetroArr.Core.Games;

namespace RetroArr.Core.Test.Games
{
    [TestFixture]
    public class PlayStationReadersTest
    {
        private string _root = null!;

        [SetUp]
        public void SetUp() => _root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "retroarr_psread_" + Path.GetRandomFileName())).FullName;

        [TearDown]
        public void TearDown() => Directory.Delete(_root, true);

        internal const string ParamJson =
            "{\"titleId\":\"PPSA01903\",\"contentVersion\":\"01.010.000\",\"localizedParameters\":{\"defaultLanguage\":\"en-US\",\"en-US\":{\"titleName\":\"FINAL FANTASY VII REMAKE™\"}}}";

        // A minimal exFAT volume: 512-byte sectors and clusters, the directories given as (name, children) or (name, content)
        internal static byte[] ExFat(params (string Path, byte[] Content)[] files)
        {
            const int cluster = 512;
            var clusters = new List<byte[]> { new byte[cluster] }; // cluster 2: root
            var dirs = new Dictionary<string, int> { [""] = 0 };
            var listings = new Dictionary<int, List<byte[]>> { [0] = new() };

            int Dir(string path)
            {
                if (dirs.TryGetValue(path, out var known)) return known;
                var parent = Dir(Path.GetDirectoryName(path)?.Replace('\\', '/') ?? "");
                clusters.Add(new byte[cluster]);
                var index = clusters.Count - 1;
                dirs[path] = index;
                listings[index] = new();
                listings[parent].Add(Entry(Path.GetFileName(path), true, (uint)(index + 2), cluster));
                return index;
            }

            foreach (var (path, content) in files)
            {
                var parent = Dir(Path.GetDirectoryName(path)?.Replace('\\', '/') ?? "");
                var first = clusters.Count;
                for (int at = 0; at < Math.Max(content.Length, 1); at += cluster)
                {
                    var c = new byte[cluster];
                    Array.Copy(content, at, c, 0, Math.Min(cluster, content.Length - at));
                    clusters.Add(c);
                }
                listings[parent].Add(Entry(Path.GetFileName(path), false, (uint)(first + 2), content.Length));
            }
            foreach (var (index, entries) in listings)
                for (int i = 0; i < entries.Count; i++) Array.Copy(entries[i], 0, clusters[index], i * 96, 96);

            var image = new byte[1024 + clusters.Count * cluster];
            Encoding.ASCII.GetBytes("EXFAT   ").CopyTo(image, 3);
            BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(80), 1);   // FAT at sector 1
            BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(88), 2);   // heap at sector 2
            BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(92), (uint)clusters.Count);
            BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(96), 2);   // root in cluster 2
            image[108] = 9;
            image[109] = 0;
            BinaryPrimitives.WriteUInt32LittleEndian(image.AsSpan(512 + 2 * 4), 0xFFFFFFFF); // root chain ends at once
            for (int i = 0; i < clusters.Count; i++) clusters[i].CopyTo(image, 1024 + i * cluster);
            return image;
        }

        private static byte[] Entry(string name, bool directory, uint firstCluster, long length)
        {
            var e = new byte[96];
            e[0] = 0x85;
            e[1] = 2;
            BinaryPrimitives.WriteUInt16LittleEndian(e.AsSpan(4), (ushort)(directory ? 0x10 : 0x20));
            e[32] = 0xC0;
            e[33] = 0x03; // allocation possible, no FAT chain
            e[35] = (byte)name.Length;
            BinaryPrimitives.WriteUInt64LittleEndian(e.AsSpan(40), (ulong)length);
            BinaryPrimitives.WriteUInt32LittleEndian(e.AsSpan(52), firstCluster);
            BinaryPrimitives.WriteUInt64LittleEndian(e.AsSpan(56), (ulong)length);
            e[64] = 0xC1;
            Encoding.Unicode.GetBytes(name).CopyTo(e, 66);
            return e;
        }

        internal static byte[] Sfo(params (string Key, string Value)[] values)
        {
            var keys = new MemoryStream();
            var data = new MemoryStream();
            var index = new List<byte[]>();
            foreach (var (key, value) in values)
            {
                var e = new byte[16];
                var bytes = Encoding.UTF8.GetBytes(value + "\0");
                BinaryPrimitives.WriteUInt16LittleEndian(e, (ushort)keys.Length);
                BinaryPrimitives.WriteUInt16LittleEndian(e.AsSpan(2), 0x0204);
                BinaryPrimitives.WriteUInt32LittleEndian(e.AsSpan(4), (uint)bytes.Length);
                BinaryPrimitives.WriteUInt32LittleEndian(e.AsSpan(8), (uint)bytes.Length);
                BinaryPrimitives.WriteUInt32LittleEndian(e.AsSpan(12), (uint)data.Length);
                keys.Write(Encoding.ASCII.GetBytes(key + "\0"));
                data.Write(bytes);
                index.Add(e);
            }
            var keyStart = 20 + 16 * values.Length;
            var dataStart = keyStart + (int)keys.Length;
            var sfo = new byte[dataStart + data.Length];
            sfo[1] = (byte)'P'; sfo[2] = (byte)'S'; sfo[3] = (byte)'F';
            BinaryPrimitives.WriteUInt32LittleEndian(sfo.AsSpan(8), (uint)keyStart);
            BinaryPrimitives.WriteUInt32LittleEndian(sfo.AsSpan(12), (uint)dataStart);
            BinaryPrimitives.WriteUInt32LittleEndian(sfo.AsSpan(16), (uint)values.Length);
            for (int i = 0; i < index.Count; i++) index[i].CopyTo(sfo, 20 + 16 * i);
            keys.ToArray().CopyTo(sfo, keyStart);
            data.ToArray().CopyTo(sfo, dataStart);
            return sfo;
        }

        internal static byte[] Pkg(string contentId, byte[] sfo)
        {
            var pkg = new byte[0x200 + sfo.Length];
            BinaryPrimitives.WriteUInt32BigEndian(pkg, 0x7F434E54);
            BinaryPrimitives.WriteUInt32BigEndian(pkg.AsSpan(0x10), 2);
            BinaryPrimitives.WriteUInt32BigEndian(pkg.AsSpan(0x18), 0x100);
            Encoding.ASCII.GetBytes(contentId).CopyTo(pkg, 0x40);
            BinaryPrimitives.WriteUInt32BigEndian(pkg.AsSpan(0x100), 0x0001); // some other entry first
            BinaryPrimitives.WriteUInt32BigEndian(pkg.AsSpan(0x120), 0x1000);
            BinaryPrimitives.WriteUInt32BigEndian(pkg.AsSpan(0x130), 0x200);
            BinaryPrimitives.WriteUInt32BigEndian(pkg.AsSpan(0x134), (uint)sfo.Length);
            sfo.CopyTo(pkg, 0x200);
            return pkg;
        }

        private string Write(string name, byte[] content)
        {
            var path = Path.Combine(_root, name);
            File.WriteAllBytes(path, content);
            return path;
        }

        [Test]
        public void ExfatImage_GivesTheTitleFromItsParamJson_WithoutMounting()
        {
            var image = Write("ff7r.exfat", ExFat(("sce_sys/param.json", Encoding.UTF8.GetBytes(ParamJson)), ("eboot.bin", new byte[700])));
            var p = PlayStationPackageInspector.Inspect(image);
            Assert.That((p.Format, p.TitleId, p.Version, p.Title), Is.EqualTo((PlayStationFormat.ExfatImage, "PPSA01903", "01.010.000", "FINAL FANTASY VII REMAKE™")));
            Assert.That(p.ImageFile, Is.EqualTo(image));
        }

        [Test]
        public void ExfatImage_WithTheGameInAnApp0Folder()
        {
            var image = Write("x.exfat", ExFat(("PPSA01903-app0/sce_sys/param.json", Encoding.UTF8.GetBytes(ParamJson))));
            Assert.That(PlayStationPackageInspector.Inspect(image).Title, Is.EqualTo("FINAL FANTASY VII REMAKE™"));
        }

        [Test]
        public void Ps4ExfatImage_ReadsParamSfo()
        {
            var sfo = Sfo(("TITLE", "Bloodborne"), ("TITLE_ID", "CUSA00900"), ("APP_VER", "01.09"));
            var p = PlayStationPackageInspector.Inspect(Write("bb.exfat", ExFat(("sce_sys/param.sfo", sfo))));
            Assert.That((p.TitleId, p.Version, p.Title), Is.EqualTo(("CUSA00900", "01.09", "Bloodborne")));
        }

        [Test]
        public void SomethingThatIsNoExfat_GivesNothing()
        {
            Assert.That(ExFatImageReader.ReadFirst(Write("bad.exfat", new byte[2048]), "sce_sys/param.json"), Is.Null);
            Assert.That(PlayStationPackageInspector.Inspect(Write("Game [PPSA12345].exfat", new byte[10])).TitleId, Is.EqualTo("PPSA12345"));
        }

        [TestCase("gd", PlayStationContent.Game)]
        [TestCase("gp", PlayStationContent.Update)]
        [TestCase("ac", PlayStationContent.Dlc)]
        public void Ps4Pkg_ReadsItsParamSfo(string category, PlayStationContent content)
        {
            var sfo = Sfo(("CATEGORY", category), ("TITLE", "Bloodborne"), ("TITLE_ID", "CUSA00900"), ("APP_VER", "01.09"));
            var p = PlayStationPackageInspector.Inspect(Write("anything.pkg", Pkg("UP9000-CUSA00900_00-BLOODBORNE000000", sfo)));
            Assert.That((p.Format, p.TitleId, p.Title, p.Version, p.Content), Is.EqualTo((PlayStationFormat.Pkg, "CUSA00900", "Bloodborne", "01.09", content)));
            Assert.That(p.ContentId, Is.EqualTo("UP9000-CUSA00900_00-BLOODBORNE000000"));
        }

        [Test]
        public void Ps5Pkg_TakesTheIdFromTheContentIdInItsName()
        {
            var p = PlayStationPackageInspector.Inspect(Write("UP9000-PPSA26344_00-GHOST2SHIP000000.pkg", new byte[100]));
            Assert.That((p.TitleId, p.ContentId, p.Content), Is.EqualTo(("PPSA26344", "UP9000-PPSA26344_00-GHOST2SHIP000000", PlayStationContent.Game)));
        }

        [Test]
        public void ImageNamedGame_TakesTheIdFromItsFolder()
        {
            var dir = Directory.CreateDirectory(Path.Combine(_root, "PPSA01234 Astro Bot.ffpfsc")).FullName;
            var file = Path.Combine(dir, "game.ffpfsc");
            File.WriteAllText(file, "x");
            Assert.That(PlayStationPackageInspector.Inspect(file).TitleId, Is.EqualTo("PPSA01234"));
        }
    }
}
