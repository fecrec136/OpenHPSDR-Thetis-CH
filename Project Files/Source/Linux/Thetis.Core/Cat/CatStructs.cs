/*  CatStructs.cs

This file is part of a program that implements a Software-Defined Radio.

The CAT command table: for each command, whether it is active and how many
parameter characters a set, a read and an answer have (-1 = not allowed).
It is the Windows console's Console/CAT/CATStructs.xml, embedded unmodified,
so the lengths the parser accepts are the console's.

This program is free software; you can redistribute it and/or
modify it under the terms of the GNU General Public License
as published by the Free Software Foundation; either version 2
of the License, or (at your option) any later version.

*/

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Xml;

namespace Thetis.Cat
{
    public readonly record struct CatStruct(string Code, string Description, bool Active, int NSet, int NGet, int NAns);

    public static class CatStructs
    {
        private static readonly Lazy<Dictionary<string, CatStruct>> _table = new Lazy<Dictionary<string, CatStruct>>(Load);

        public static IReadOnlyDictionary<string, CatStruct> All => _table.Value;

        public static bool TryGet(string code, out CatStruct s) => _table.Value.TryGetValue(code, out s);

        private static Dictionary<string, CatStruct> Load()
        {
            var d = new Dictionary<string, CatStruct>(StringComparer.Ordinal);
            using var stream = typeof(CatStructs).Assembly.GetManifestResourceStream("CATStructs.xml")
                               ?? throw new InvalidOperationException("CATStructs.xml is not embedded");
            var doc = new XmlDocument();
            doc.Load(stream);
            foreach (XmlNode n in doc.DocumentElement.ChildNodes)
            {
                if (n.Name != "catstruct") continue;
                string code = n.Attributes?["code"]?.Value;
                if (string.IsNullOrEmpty(code)) continue;
                int Num(string name) =>
                    int.TryParse(n[name]?.InnerText.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int v) ? v : -1;
                bool active = bool.TryParse(n["active"]?.InnerText.Trim(), out bool a) && a;
                d[code] = new CatStruct(code, n["desc"]?.InnerText.Trim(), active, Num("nsetparms"), Num("ngetparms"), Num("nansparms"));
            }
            return d;
        }
    }
}
