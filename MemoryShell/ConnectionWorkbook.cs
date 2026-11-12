using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;

namespace MemoryShell;

internal sealed class ConnectionWorkbook(string?[][] devices, string?[][] keyParts) : IDisposable
{
    private bool disposed;
    private const string SheetNamespace = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private const int KeyPartLength = 16000;
    private static readonly Regex ExcelEscape = new(@"_x[0-9a-fA-F]{4}_", RegexOptions.CultureInvariant);

    public static ConnectionWorkbook Capture(WorkspaceStore store)
    {
        var devices = new List<string?[]>
        {
            new string?[] { L10n.Get("S65D8F92232AE"), L10n.Get("SAB2F31F30ACF"), L10n.Get("S399EE6934772"), L10n.Get("S715A85D91000"), L10n.Get("SE71AC32B544B"), L10n.Get("S1A3F0617D6DE"), L10n.Get("S5F180674EC89"), L10n.Get("SA518B48E32DD"),
             L10n.Get("SF9C0147D55CD"), L10n.Get("SB9AF2DE88D90"), L10n.Get("S1CB7E646AC88"), L10n.Get("S025F931C5D8F"), L10n.Get("S82CFBC66B5DC"),
             L10n.Get("SA6A4530FE853"), L10n.Get("S9129442BF400"), L10n.Get("S01B6FCF6A966"), L10n.Get("SD88DE0D63127"), L10n.Get("S297B0513F324"), "RD Gateway" }
        };
        var keys = new List<string?[]> { new string?[] { L10n.Get("S5200C82FE4D2"), L10n.Get("S65D8F92232AE"), L10n.Get("S05B36669C4AD"), L10n.Get("SA505475158E4"), L10n.Get("S3880FF45B5DA"), L10n.Get("S6E32E04CA7F6") } };

        string? AddKey(string device, string purpose, byte[]? bytes)
        {
            if (bytes is null)
                return null;
            var id = $"KEY-{keys.Count.ToString(CultureInfo.InvariantCulture)}";
            string content;
            string encoding;
            try
            {
                content = new UTF8Encoding(false, true).GetString(bytes);
                encoding = "UTF-8";
            }
            catch (DecoderFallbackException)
            {
                content = Convert.ToBase64String(bytes);
                encoding = "Base64";
            }
            var offset = 0;
            var part = 1;
            while (offset < content.Length)
            {
                var count = Math.Min(KeyPartLength, content.Length - offset);
                if (offset + count < content.Length && char.IsHighSurrogate(content[offset + count - 1]))
                    count--;
                keys.Add([id, device, purpose, (part++).ToString(CultureInfo.InvariantCulture), encoding, content.Substring(offset, count)]);
                offset += count;
            }
            return id;
        }

        foreach (var entry in store.Connections)
        {
            var profile = entry.Profile;
            var proxy = profile.Proxy;
            var jump = profile.Jump;
            devices.Add([profile.Name, "SSH", profile.Group, profile.Host, profile.Port.ToString(CultureInfo.InvariantCulture),
                profile.Username, entry.Secret, AddKey(profile.Name, "SSH", entry.PrivateKey),
                proxy?.Kind.ToString(), proxy?.Host, proxy?.Port.ToString(CultureInfo.InvariantCulture), proxy?.Username, proxy?.Password,
                jump?.Host, jump?.Port.ToString(CultureInfo.InvariantCulture), jump?.Username, jump?.Password,
                AddKey(profile.Name, L10n.Get("SE7988FBD7AAB"), jump?.PrivateKey), null]);
        }
        foreach (var key in store.KeyWallet)
        {
            var row = new string?[19];
            row[0] = key.Name;
            row[1] = L10n.Get("KeyWallet");
            row[6] = key.Passphrase;
            row[7] = AddKey(key.Name, L10n.Get("KeyWallet"), key.PrivateKey);
            devices.Add(row);
        }
        foreach (var profile in store.RemoteDesktops)
        {
            var row = new string?[19];
            row[0] = profile.Name;
            row[1] = "RDP";
            row[3] = profile.Host;
            row[4] = profile.Port.ToString(CultureInfo.InvariantCulture);
            row[5] = profile.Username;
            row[18] = profile.Gateway;
            devices.Add(row);
        }
        return new ConnectionWorkbook(devices.ToArray(), keys.ToArray());
    }

    public void Save(string path)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        var destination = Path.GetFullPath(path);
        var stage = Path.Combine(Path.GetDirectoryName(destination)!, $".memoryshell-{Guid.NewGuid():N}.xlsx.tmp");
        try
        {
            using (var stream = new FileStream(stage, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var zip = new ZipArchive(stream, ZipArchiveMode.Create))
            {
                WritePart(zip, "[Content_Types].xml", """
                    <Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types">
                      <Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/>
                      <Default Extension="xml" ContentType="application/xml"/>
                      <Override PartName="/xl/workbook.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml"/>
                      <Override PartName="/xl/worksheets/sheet1.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml"/>
                      <Override PartName="/xl/worksheets/sheet2.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml"/>
                    </Types>
                    """);
                WritePart(zip, "_rels/.rels", """
                    <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
                      <Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="xl/workbook.xml"/>
                    </Relationships>
                    """);
                WritePart(zip, "xl/workbook.xml", $"""
                    <workbook xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships">
                      <bookViews><workbookView/></bookViews>
                      <sheets><sheet name="{System.Security.SecurityElement.Escape(L10n.Get("S82479CB6CA73"))}" sheetId="1" r:id="rId1"/><sheet name="{System.Security.SecurityElement.Escape(L10n.Get("S2637E8166D92"))}" sheetId="2" r:id="rId2"/></sheets>
                    </workbook>
                    """);
                WritePart(zip, "xl/_rels/workbook.xml.rels", """
                    <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
                      <Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet" Target="worksheets/sheet1.xml"/>
                      <Relationship Id="rId2" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet" Target="worksheets/sheet2.xml"/>
                    </Relationships>
                    """);
                WriteSheet(zip, "xl/worksheets/sheet1.xml", devices);
                WriteSheet(zip, "xl/worksheets/sheet2.xml", keyParts);
            }
            File.Move(stage, destination, overwrite: true);
        }
        finally { if (File.Exists(stage)) File.Delete(stage); }
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        foreach (var row in devices) Array.Clear(row);
        foreach (var row in keyParts) Array.Clear(row);
    }

    private static void WritePart(ZipArchive zip, string name, string content)
    {
        using var writer = new StreamWriter(zip.CreateEntry(name).Open(), new UTF8Encoding(false));
        writer.Write(content);
    }

    private static void WriteSheet(ZipArchive zip, string name, string?[][] rows)
    {
        using var stream = zip.CreateEntry(name).Open();
        using var xml = XmlWriter.Create(stream, new XmlWriterSettings { Encoding = new UTF8Encoding(false) });
        xml.WriteStartElement("worksheet", SheetNamespace);
        xml.WriteStartElement("sheetViews", SheetNamespace);
        xml.WriteStartElement("sheetView", SheetNamespace);
        xml.WriteAttributeString("workbookViewId", "0");
        xml.WriteStartElement("pane", SheetNamespace);
        xml.WriteAttributeString("ySplit", "1");
        xml.WriteAttributeString("topLeftCell", "A2");
        xml.WriteAttributeString("activePane", "bottomLeft");
        xml.WriteAttributeString("state", "frozen");
        xml.WriteEndElement();
        xml.WriteEndElement();
        xml.WriteEndElement();
        xml.WriteStartElement("sheetData", SheetNamespace);
        for (var r = 0; r < rows.Length; r++)
        {
            xml.WriteStartElement("row", SheetNamespace);
            var row = (r + 1).ToString(CultureInfo.InvariantCulture);
            xml.WriteAttributeString("r", row);
            for (var c = 0; c < rows[r].Length; c++)
            {
                if (rows[r][c] is not { } value)
                    continue;
                if (value.Length > 32767)
                    throw new InvalidDataException(L10n.Get("S0BB0BF378308"));
                xml.WriteStartElement("c", SheetNamespace);
                xml.WriteAttributeString("r", $"{(char)('A' + c)}{row}");
                xml.WriteAttributeString("t", "inlineStr");
                xml.WriteStartElement("is", SheetNamespace);
                xml.WriteStartElement("t", SheetNamespace);
                xml.WriteAttributeString("xml", "space", "http://www.w3.org/XML/1998/namespace", "preserve");
                xml.WriteString(EscapeText(value));
                xml.WriteEndElement();
                xml.WriteEndElement();
                xml.WriteEndElement();
            }
            xml.WriteEndElement();
        }
        xml.WriteEndElement();
        xml.WriteEndElement();
    }

    private static string EscapeText(string input)
    {
        // Inline strings are always literal, including cells beginning with =, +, - or @.
        var escaped = ExcelEscape.Replace(input, match => "_x005F_" + match.Value[1..]);
        var output = new StringBuilder(escaped.Length);
        for (var i = 0; i < escaped.Length; i++)
        {
            var c = escaped[i];
            if (char.IsHighSurrogate(c) && i + 1 < escaped.Length && char.IsLowSurrogate(escaped[i + 1]))
                output.Append(c).Append(escaped[++i]);
            else if (XmlConvert.IsXmlChar(c) && c != '\r')
                output.Append(c);
            else
                output.Append("_x").Append(((int)c).ToString("X4", CultureInfo.InvariantCulture)).Append('_');
        }
        return output.ToString();
    }
}


