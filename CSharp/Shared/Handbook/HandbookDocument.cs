using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Xml.Linq;

namespace Touhou.Handbook
{
    internal enum HandbookBlockKind { Heading, Text, Items, Recipe, Steps, Note, Link }

    internal sealed class HandbookItem
    {
        internal string Identifier = "", Text = "", Key = "", Page = "";
        internal int Amount = 1;
    }

    internal sealed class HandbookText
    {
        internal string Text = "", Key = "";
    }

    internal sealed class HandbookBlock
    {
        internal HandbookBlockKind Kind;
        internal string Text = "", Key = "", Source = "", Other = "", Station = "", Page = "";
        internal readonly List<HandbookItem> Items = new();
        internal readonly List<HandbookText> Steps = new();
    }

    internal sealed class HandbookPage
    {
        internal string Id, Title, TitleKey = "";
        internal readonly List<HandbookBlock> Blocks = new();
    }

    internal sealed class HandbookDocument
    {
        internal string Title = "Handbook", TitleKey = "", StartPage = "";
        internal readonly List<HandbookPage> Pages = new();
        internal readonly List<string> Warnings = new();
        internal HandbookPage Find(string id) => Pages.Find(p => p.Id == id);
        internal static string Attribute(XElement e, string name, string fallback = "")
            => ((string)e?.Attribute(name) ?? fallback).Trim();

        internal static HandbookDocument Parse(XElement root)
        {
            var book = new HandbookDocument();
            if (root == null) { book.Warnings.Add("没有手册内容"); return book; }
            book.Title = Attribute(root, "title", book.Title);
            book.TitleKey = Attribute(root, "titlekey");
            book.StartPage = Attribute(root, "startpage");
            var ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (var node in (root.Element("Pages")?.Elements("Page") ?? Enumerable.Empty<XElement>()).Take(64))
            {
                string id = Attribute(node, "id");
                if (id.Length == 0 || !ids.Add(id))
                { book.Warnings.Add("章节 id 缺失或重复：" + id); continue; }
                var page = new HandbookPage
                { Id = id, Title = Attribute(node, "title", id), TitleKey = Attribute(node, "titlekey") };
                foreach (var child in node.Elements().Take(256))
                {
                    if (!Enum.TryParse(child.Name.LocalName, true, out HandbookBlockKind kind) ||
                        !Enum.IsDefined(typeof(HandbookBlockKind), kind))
                    { book.Warnings.Add("未知内容块：" + child.Name.LocalName); continue; }
                    var block = new HandbookBlock
                    {
                        Kind = kind, Text = Attribute(child, "text"), Key = Attribute(child, "key"),
                        Source = Attribute(child, "source"), Other = Attribute(child, "other"),
                        Station = Attribute(child, "station", "Alchemy_Table"), Page = Attribute(child, "page")
                    };
                    foreach (var item in child.Elements("Item").Take(32))
                    {
                        string identifier = Attribute(item, "identifier");
                        if (identifier.Length == 0) { book.Warnings.Add("物品块缺少 identifier"); continue; }
                        int.TryParse(Attribute(item, "amount", "1"), NumberStyles.Integer,
                            CultureInfo.InvariantCulture, out int amount);
                        block.Items.Add(new HandbookItem
                        {
                            Identifier = identifier, Amount = Math.Clamp(amount, 1, 9999),
                            Text = Attribute(item, "text"), Key = Attribute(item, "key"), Page = Attribute(item, "page")
                        });
                    }
                    block.Steps.AddRange(child.Elements("Step").Take(32).Select(s => new HandbookText
                        { Text = Attribute(s, "text"), Key = Attribute(s, "key") }));
                    if (kind == HandbookBlockKind.Recipe && block.Source.Length == 0)
                    { book.Warnings.Add("配方块缺少 source"); continue; }
                    page.Blocks.Add(block);
                }
                book.Pages.Add(page);
            }
            if (book.Find(book.StartPage) == null) book.StartPage = book.Pages.FirstOrDefault()?.Id ?? "";
            foreach (var block in book.Pages.SelectMany(p => p.Blocks))
            {
                foreach (string target in block.Items.Select(i => i.Page).Append(block.Page).Where(s => s.Length > 0))
                    if (book.Find(target) == null) book.Warnings.Add("跳转章节不存在：" + target);
            }
            return book;
        }
    }

    internal sealed class HandbookNavigation
    {
        private readonly HandbookDocument book;
        private readonly Stack<string> history = new();
        private readonly Dictionary<string, float> scroll = new();
        internal string Current { get; private set; }
        internal bool CanGoBack => history.Count > 0;
        internal HandbookNavigation(HandbookDocument book) { this.book = book; Current = book.StartPage; }
        internal bool Go(string id, bool remember = true)
        {
            if (book.Find(id) == null || id == Current) return false;
            if (remember && Current.Length > 0) history.Push(Current);
            Current = id;
            return true;
        }
        internal bool Back() => history.Count > 0 && Go(history.Pop(), false);
        internal string Adjacent(int direction)
        {
            int index = book.Pages.FindIndex(p => p.Id == Current) + direction;
            return index >= 0 && index < book.Pages.Count ? book.Pages[index].Id : null;
        }
        internal float Scroll => scroll.TryGetValue(Current, out float value) ? value : 0f;
        internal void RememberScroll(float value)
            => scroll[Current] = float.IsFinite(value) ? Math.Clamp(value, 0f, 1f) : 0f;
    }

    internal sealed class HandbookRecipeRule
    {
        internal string Identifier;
        internal int Amount;
        internal float MinCondition, MaxCondition, Weight;
        internal string[] Stations = Array.Empty<string>(), Others = Array.Empty<string>();
    }

    internal static class HandbookRecipes
    {
        // 只做查询，不造物品，也不把副材料误当成不消耗的催化剂。
        internal static List<HandbookRecipeRule> Match(IEnumerable<HandbookRecipeRule> rules,
            string station, IEnumerable<string> stationTags, string other, IEnumerable<string> otherTags,
            float condition = 1f)
        {
            var stations = new HashSet<string>(stationTags, StringComparer.OrdinalIgnoreCase) { station };
            var others = new HashSet<string>(otherTags, StringComparer.OrdinalIgnoreCase) { other };
            return rules.Where(r => r.MinCondition <= condition && r.MaxCondition >= condition &&
                (r.Stations.Length == 0 || r.Stations.Any(stations.Contains)) &&
                (r.Others.Length == 0 || r.Others.Any(others.Contains))).ToList();
        }
    }
}
