using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Xml.Linq;

namespace Touhou.VirtualPantry
{
    // 只存"已经落账"的虚拟库存，实物和在途操作一概不进来。
    internal sealed class PantryLedger
    {
        internal const int MaxBuckets = 64;
        internal const int MaxStock = 1000000;
        internal sealed class Bucket
        {
            internal uint Key;
            internal XElement Template;
            internal string Identity;
            internal int Count;
            internal string Identifier => (string)Template.Attribute("identifier") ?? "";
        }

        internal readonly List<Bucket> Buckets = new();
        private uint nextKey = 1;
        internal int Total => Buckets.Sum(b => b.Count);

        // 抹掉每局都会变的字段，剩下的 XML 才是"同一个保存状态"的身份。
        internal static XElement Normalize(XElement item)
        {
            var copy = new XElement(item);
            foreach (string name in new[] { "ID", "rect", "linked", "markedfordeconstruction" })
            {
                copy.Attribute(name)?.Remove();
            }
            return copy;
        }

        internal Bucket FindOrAdd(XElement item)
        {
            XElement template = Normalize(item);
            string identity = template.ToString(SaveOptions.DisableFormatting);
            var existing = Buckets.FirstOrDefault(b => b.Identity == identity);
            if (existing != null) { return existing; }
            // 到上限就返回 null，让调用方把那件东西继续留成实物，绝不丢。
            if (Buckets.Count >= MaxBuckets || nextKey == uint.MaxValue || identity.Length > 65536) { return null; }
            var bucket = new Bucket { Key = nextKey++, Template = template, Identity = identity };
            Buckets.Add(bucket);
            return bucket;
        }

        internal void Prune(HashSet<uint> physical)
            => Buckets.RemoveAll(b => b.Count == 0 && !physical.Contains(b.Key));

        internal XElement Save(bool suspended, bool draining)
            => new XElement("TouhouVirtualPantry",
                new XAttribute("version", 1), new XAttribute("nextkey", nextKey),
                new XAttribute("suspended", suspended), new XAttribute("draining", draining),
                Buckets.Where(b => b.Count > 0).Select(b => new XElement("Stock",
                    new XAttribute("key", b.Key), new XAttribute("count", b.Count), new XElement(b.Template))));

        // 先整套校验再替换：坏数据/未来版本不能只读进去一半。
        internal static PantryLedger Load(XElement state)
        {
            if ((int?)state.Attribute("version") != 1) { throw new FormatException("Unsupported pantry save version."); }
            var result = new PantryLedger();
            var keys = new HashSet<uint>();
            var identities = new HashSet<string>(StringComparer.Ordinal);
            foreach (XElement row in state.Elements())
            {
                if (row.Name != "Stock" || row.Elements().Count() != 1) { throw new FormatException("Invalid stock record."); }
                uint key = uint.Parse((string)row.Attribute("key") ?? "", CultureInfo.InvariantCulture);
                int count = int.Parse((string)row.Attribute("count") ?? "", CultureInfo.InvariantCulture);
                XElement template = Normalize(row.Elements().Single());
                string identity = template.ToString(SaveOptions.DisableFormatting);
                if (key == 0 || key == uint.MaxValue || count <= 0 || count > MaxStock ||
                    template.Name != "Item" || string.IsNullOrWhiteSpace((string)template.Attribute("identifier")) ||
                    identity.Length > 65536 || !keys.Add(key) || !identities.Add(identity) || result.Buckets.Count == MaxBuckets)
                { throw new FormatException("Invalid or duplicate stock record."); }
                result.Buckets.Add(new Bucket { Key = key, Count = count, Template = template, Identity = identity });
                if (result.Total > MaxStock) { throw new FormatException("Stock limit exceeded."); }
            }
            result.nextKey = uint.Parse((string)state.Attribute("nextkey") ?? "", CultureInfo.InvariantCulture);
            if (result.nextKey == 0 || keys.Any(k => k >= result.nextKey)) { throw new FormatException("Invalid stock sequence."); }
            return result;
        }
    }
}
