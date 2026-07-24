using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml.Linq;

namespace FiveOS.Services;

public enum VehicleMetaKind
{
    Other,
    Vehicles,
    Handling,
    Carvariations,
}

public sealed class VehicleMetaFormFields
{
    public string ModelName { get; set; } = "";
    public string GameName { get; set; } = "";
    public string VehicleMake { get; set; } = "";
    public string VehicleClass { get; set; } = "VC_SEDAN";
    public string HandlingId { get; set; } = "";
    public string Frequency { get; set; } = "100";
    public string Flags { get; set; } = "";
    public string Mass { get; set; } = "";
    public string InitialDriveForce { get; set; } = "";
    public string BrakeForce { get; set; } = "";
    public string TractionCurveMax { get; set; } = "";
    public string TractionCurveMin { get; set; } = "";
    public string TopSpeedKph { get; set; } = "";
    public string DownforceModifier { get; set; } = "";
    public string Color1 { get; set; } = "0";
    public string Color2 { get; set; } = "0";
    public string Pearlescent { get; set; } = "0";
    public string Kits { get; set; } = "";
}

public static class VehicleMetaFormService
{
    public static VehicleMetaKind DetectKind(string fileName)
    {
        var n = Path.GetFileName(fileName).ToLowerInvariant();
        if (n.Contains("carvariation")) return VehicleMetaKind.Carvariations;
        if (n.Contains("handling")) return VehicleMetaKind.Handling;
        if (n.Contains("vehicles")) return VehicleMetaKind.Vehicles;
        return VehicleMetaKind.Other;
    }

    public static bool SupportsForm(VehicleMetaKind kind)
        => kind is VehicleMetaKind.Vehicles or VehicleMetaKind.Handling or VehicleMetaKind.Carvariations;

    public static string? TryValidateXml(string text)
    {
        try { XDocument.Parse(text); return null; }
        catch (Exception ex) { return ex.Message; }
    }

    public static VehicleMetaFormFields LoadFields(string xml, VehicleMetaKind kind)
    {
        var fields = new VehicleMetaFormFields();
        if (string.IsNullOrWhiteSpace(xml)) return fields;
        XDocument doc;
        try { doc = XDocument.Parse(xml); }
        catch { return fields; }

        return kind switch
        {
            VehicleMetaKind.Vehicles => LoadVehicles(doc, fields),
            VehicleMetaKind.Handling => LoadHandling(doc, fields),
            VehicleMetaKind.Carvariations => LoadCarvariations(doc, fields),
            _ => fields,
        };
    }

    public static string ApplyFields(string xml, VehicleMetaKind kind, VehicleMetaFormFields fields)
    {
        XDocument doc;
        try { doc = XDocument.Parse(string.IsNullOrWhiteSpace(xml) ? MinimalStub(kind) : xml); }
        catch { doc = XDocument.Parse(MinimalStub(kind)); }

        switch (kind)
        {
            case VehicleMetaKind.Vehicles: ApplyVehicles(doc, fields); break;
            case VehicleMetaKind.Handling: ApplyHandling(doc, fields); break;
            case VehicleMetaKind.Carvariations: ApplyCarvariations(doc, fields); break;
        }

        var sb = new StringBuilder();
        using (var writer = new StringWriter(sb))
            doc.Save(writer, SaveOptions.None);
        var text = sb.ToString();
        if (text.StartsWith("<?xml", StringComparison.OrdinalIgnoreCase))
            return text;
        return "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n" + text;
    }

    public static string MinimalStub(VehicleMetaKind kind) => kind switch
    {
        VehicleMetaKind.Vehicles =>
            "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n" +
            "<CVehicleModelInfo__InitDataList>\n  <InitDatas>\n    <Item>\n" +
            "      <modelName>car</modelName>\n      <txdName>car</txdName>\n" +
            "      <handlingId>CAR</handlingId>\n      <gameName>CAR</gameName>\n" +
            "      <vehicleMakeName />\n      <vehicleClass>VC_SEDAN</vehicleClass>\n" +
            "      <frequency>100</frequency>\n      <flags />\n    </Item>\n  </InitDatas>\n</CVehicleModelInfo__InitDataList>\n",
        VehicleMetaKind.Handling =>
            "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n" +
            "<CHandlingDataMgr>\n  <HandlingData>\n    <Item type=\"CHandlingData\">\n" +
            "      <handlingName>CAR</handlingName>\n" +
            "      <fMass value=\"1400.000000\" />\n" +
            "      <fInitialDriveForce value=\"0.260000\" />\n" +
            "      <fBrakeForce value=\"0.900000\" />\n" +
            "      <fTractionCurveMax value=\"2.350000\" />\n" +
            "      <fTractionCurveMin value=\"2.050000\" />\n" +
            "      <fInitialDriveMaxFlatVel value=\"145.000000\" />\n" +
            "      <fDownforceModifier value=\"1.000000\" />\n" +
            "    </Item>\n  </HandlingData>\n</CHandlingDataMgr>\n",
        VehicleMetaKind.Carvariations =>
            "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n" +
            "<CVehicleModelInfoVariation>\n  <variationData>\n    <Item>\n" +
            "      <modelName>car</modelName>\n" +
            "      <colors>\n        <Item>\n          <indices content=\"char_array\">\n" +
            "            0\n            0\n            0\n            0\n          </indices>\n" +
            "        </Item>\n      </colors>\n" +
            "      <kits>\n        <Item>0_default_modkit</Item>\n      </kits>\n" +
            "    </Item>\n  </variationData>\n</CVehicleModelInfoVariation>\n",
        _ => "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n<root />\n",
    };

    private static VehicleMetaFormFields LoadVehicles(XDocument doc, VehicleMetaFormFields f)
    {
        var item = FirstItem(doc) ?? doc.Root;
        if (item == null) return f;
        f.ModelName = TextOf(item, "modelName");
        f.GameName = TextOf(item, "gameName");
        f.VehicleMake = TextOf(item, "vehicleMakeName");
        f.VehicleClass = TextOf(item, "vehicleClass");
        if (string.IsNullOrWhiteSpace(f.VehicleClass)) f.VehicleClass = "VC_SEDAN";
        f.HandlingId = TextOf(item, "handlingId");
        f.Frequency = TextOf(item, "frequency");
        if (string.IsNullOrWhiteSpace(f.Frequency)) f.Frequency = "100";
        f.Flags = TextOf(item, "flags");
        return f;
    }

    private static void ApplyVehicles(XDocument doc, VehicleMetaFormFields f)
    {
        var item = FirstItem(doc);
        if (item == null) return;
        SetText(item, "modelName", f.ModelName);
        SetText(item, "txdName", string.IsNullOrWhiteSpace(f.ModelName) ? TextOf(item, "txdName") : f.ModelName);
        SetText(item, "gameName", f.GameName);
        SetText(item, "vehicleMakeName", f.VehicleMake);
        SetText(item, "vehicleClass", f.VehicleClass);
        SetText(item, "handlingId", f.HandlingId);
        SetText(item, "frequency", f.Frequency);
        SetText(item, "flags", f.Flags);
    }

    private static VehicleMetaFormFields LoadHandling(XDocument doc, VehicleMetaFormFields f)
    {
        var item = FirstItem(doc) ?? doc.Root;
        if (item == null) return f;
        f.HandlingId = TextOf(item, "handlingName");
        f.Mass = AttrOrText(item, "fMass");
        f.InitialDriveForce = AttrOrText(item, "fInitialDriveForce");
        f.BrakeForce = AttrOrText(item, "fBrakeForce");
        f.TractionCurveMax = AttrOrText(item, "fTractionCurveMax");
        f.TractionCurveMin = AttrOrText(item, "fTractionCurveMin");
        f.TopSpeedKph = AttrOrText(item, "fInitialDriveMaxFlatVel");
        f.DownforceModifier = AttrOrText(item, "fDownforceModifier");
        return f;
    }

    private static void ApplyHandling(XDocument doc, VehicleMetaFormFields f)
    {
        var item = FirstItem(doc);
        if (item == null) return;
        SetText(item, "handlingName", f.HandlingId);
        SetAttrValue(item, "fMass", f.Mass);
        SetAttrValue(item, "fInitialDriveForce", f.InitialDriveForce);
        SetAttrValue(item, "fBrakeForce", f.BrakeForce);
        SetAttrValue(item, "fTractionCurveMax", f.TractionCurveMax);
        SetAttrValue(item, "fTractionCurveMin", f.TractionCurveMin);
        SetAttrValue(item, "fInitialDriveMaxFlatVel", f.TopSpeedKph);
        SetAttrValue(item, "fDownforceModifier", f.DownforceModifier);
    }

    private static VehicleMetaFormFields LoadCarvariations(XDocument doc, VehicleMetaFormFields f)
    {
        var item = FirstItem(doc) ?? doc.Root;
        if (item == null) return f;
        f.ModelName = TextOf(item, "modelName");
        var indices = Descendants(item, "indices").FirstOrDefault();
        if (indices != null)
        {
            var nums = (indices.Value ?? "")
                .Split(new[] { ' ', '\t', '\r', '\n', ',' }, StringSplitOptions.RemoveEmptyEntries);
            if (nums.Length > 0) f.Color1 = nums[0];
            if (nums.Length > 1) f.Color2 = nums[1];
            if (nums.Length > 2) f.Pearlescent = nums[2];
        }
        var kits = Descendants(item, "kits").FirstOrDefault();
        if (kits != null)
        {
            var parts = kits.Elements().Select(e =>
            {
                var t = (e.Value ?? "").Trim();
                var us = t.IndexOf('_');
                return us > 0 && int.TryParse(t.Substring(0, us), out _) ? t[..us] : t;
            }).Where(s => s.Length > 0);
            f.Kits = string.Join(", ", parts);
        }
        return f;
    }

    private static void ApplyCarvariations(XDocument doc, VehicleMetaFormFields f)
    {
        var item = FirstItem(doc);
        if (item == null) return;
        SetText(item, "modelName", f.ModelName);
        var indices = Descendants(item, "indices").FirstOrDefault();
        if (indices != null)
        {
            var c1 = ParseInt(f.Color1, 0);
            var c2 = ParseInt(f.Color2, 0);
            var pe = ParseInt(f.Pearlescent, 0);
            var rest = (indices.Value ?? "")
                .Split(new[] { ' ', '\t', '\r', '\n', ',' }, StringSplitOptions.RemoveEmptyEntries)
                .Skip(3).ToList();
            while (rest.Count < 1) rest.Add("0");
            indices.Value = "\n            " + c1 + "\n            " + c2 + "\n            " + pe
                + string.Concat(rest.Select(x => "\n            " + x)) + "\n          ";
        }
        var kits = Descendants(item, "kits").FirstOrDefault();
        if (kits != null)
        {
            kits.RemoveNodes();
            foreach (var part in (f.Kits ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (part.Length == 0) continue;
                var label = part.Contains('_') ? part : part + "_modkit";
                kits.Add(new XElement("Item", label));
            }
            if (!kits.Elements().Any())
                kits.Add(new XElement("Item", "0_default_modkit"));
        }
    }

    private static XElement? FirstItem(XDocument doc)
        => doc.Descendants().FirstOrDefault(e => Local(e) == "Item");

    private static IEnumerable<XElement> Descendants(XElement root, string local)
        => root.Descendants().Where(e => Local(e) == local);

    private static string Local(XElement e) => e.Name.LocalName;

    private static string TextOf(XElement parent, string local)
    {
        var el = parent.Elements().FirstOrDefault(e => Local(e) == local)
              ?? parent.Descendants().FirstOrDefault(e => Local(e) == local);
        return (el?.Value ?? "").Trim();
    }

    private static string AttrOrText(XElement parent, string local)
    {
        var el = parent.Elements().FirstOrDefault(e => Local(e) == local)
              ?? parent.Descendants().FirstOrDefault(e => Local(e) == local);
        if (el == null) return "";
        var a = el.Attribute("value");
        if (a != null) return a.Value.Trim();
        return (el.Value ?? "").Trim();
    }

    private static void SetText(XElement parent, string local, string value)
    {
        var el = parent.Elements().FirstOrDefault(e => Local(e) == local);
        if (el == null)
        {
            el = new XElement(local);
            parent.Add(el);
        }
        el.Value = value ?? "";
    }

    private static void SetAttrValue(XElement parent, string local, string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return;
        var el = parent.Elements().FirstOrDefault(e => Local(e) == local);
        if (el == null)
        {
            el = new XElement(local);
            parent.Add(el);
        }
        if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var d))
            el.SetAttributeValue("value", value.Trim());
        else
            el.SetAttributeValue("value", d.ToString("0.000000", CultureInfo.InvariantCulture));
        el.RemoveNodes();
    }

    private static int ParseInt(string? s, int fallback)
        => int.TryParse((s ?? "").Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : fallback;
}
