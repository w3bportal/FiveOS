// FingerTest — scores the finger retarget numerically. See FingerTest.csproj.
//
// Usage: fingertest <clip.fbx>
//
// Output per hand/digit/joint: source vs target curl-angle correlation and
// range, plus a crossing detector. "Curl angle" here is the INTERIOR bend at a
// joint (angle between the incoming and outgoing bone directions in world
// space) — rig-agnostic, so source and GTA are directly comparable.

using System.Numerics;
using Assimp;
using FiveOS.Services;
using Q = System.Numerics.Quaternion;
using V = System.Numerics.Vector3;
using Matrix4x4 = System.Numerics.Matrix4x4;

if (args.Length < 1 || !File.Exists(args[0]))
{
    Console.Error.WriteLine("usage: fingertest <clip.fbx>");
    return 2;
}

Environment.SetEnvironmentVariable("FIVEOS_FINGER_RETARGET", "1");
var intentCsv = Path.Combine(Path.GetTempPath(), $"fingertest-intent-{Environment.ProcessId}.csv");
if (File.Exists(intentCsv)) File.Delete(intentCsv);
Environment.SetEnvironmentVariable("FIVEOS_FINGER_CSV", intentCsv);

// ─── run the real retarget ───────────────────────────────────────────────
using var ctx = new AssimpContext();
var scene = ctx.ImportFile(args[0], PostProcessSteps.None);
if (scene.AnimationCount == 0) { Console.Error.WriteLine("no animations in file"); return 2; }
var anim = scene.Animations[0];
double tps = anim.TicksPerSecond > 1 ? anim.TicksPerSecond : 30.0;
int fps = 30;
int frames = Math.Max(2, (int)Math.Round(anim.DurationInTicks / tps * fps));
Console.WriteLine($"clip: {Path.GetFileName(args[0])}  {frames} frames @ {fps}fps (tps={tps:0.#})");

var warnings = new List<string>();
var tracks = AnimRetarget.Retarget(scene, anim, tps, frames, fps,
    out var mapped, out var unmapped, out _, warnings);
foreach (var w in warnings) Console.WriteLine("  warn: " + w);
if (tracks is null || tracks.Count == 0) { Console.Error.WriteLine("retarget produced nothing"); return 2; }
Console.WriteLine($"tracks: {tracks.Count}  (mapped {mapped.Count}, unmapped {unmapped.Count})");

var trackByTag = tracks.ToDictionary(t => t.BoneTag, t => t.PerFrame);

// ─── FK tables ───────────────────────────────────────────────────────────
// Minimal node table: name → (parent, local rest TRS, channel).
static Dictionary<string, (string parent, V pos, Q rot, V scale)> BuildTable(Node root)
{
    var map = new Dictionary<string, (string, V, Q, V)>(StringComparer.Ordinal);
    void Walk(Node n, string parent)
    {
        var m = n.Transform; m.Transpose();   // Assimp is row-major for System.Numerics
        var mm = new Matrix4x4(
            m.A1, m.A2, m.A3, m.A4, m.B1, m.B2, m.B3, m.B4,
            m.C1, m.C2, m.C3, m.C4, m.D1, m.D2, m.D3, m.D4);
        Matrix4x4.Decompose(mm, out var s, out var r, out var p);
        if (!map.ContainsKey(n.Name)) map[n.Name] = (parent, p, Q.Normalize(r), s);
        for (int i = 0; i < n.ChildCount; i++) Walk(n.Children[i], n.Name);
    }
    Walk(root, "");
    return map;
}

static Q SampleRot(NodeAnimationChannel ch, double ticks)
{
    if (ch.RotationKeyCount == 0) return Q.Identity;
    var keys = ch.RotationKeys;
    int i = 0;
    while (i < keys.Count - 1 && keys[i + 1].Time <= ticks) i++;
    var a = keys[i];
    var b = keys[Math.Min(i + 1, keys.Count - 1)];
    var qa = Q.Normalize(new Q(a.Value.X, a.Value.Y, a.Value.Z, a.Value.W));
    var qb = Q.Normalize(new Q(b.Value.X, b.Value.Y, b.Value.Z, b.Value.W));
    double span = b.Time - a.Time;
    float t = span > 1e-9 ? (float)Math.Clamp((ticks - a.Time) / span, 0, 1) : 0f;
    return Q.Normalize(Q.Slerp(qa, qb, t));
}

static V SamplePos(NodeAnimationChannel ch, double ticks, V rest)
{
    if (ch.PositionKeyCount == 0) return rest;
    var keys = ch.PositionKeys;
    int i = 0;
    while (i < keys.Count - 1 && keys[i + 1].Time <= ticks) i++;
    var a = keys[i];
    var b = keys[Math.Min(i + 1, keys.Count - 1)];
    double span = b.Time - a.Time;
    float t = span > 1e-9 ? (float)Math.Clamp((ticks - a.Time) / span, 0, 1) : 0f;
    return V.Lerp(new V(a.Value.X, a.Value.Y, a.Value.Z), new V(b.Value.X, b.Value.Y, b.Value.Z), t);
}

// World positions of `wanted` nodes per frame, standard FBX FK (a channel
// replaces the node's local transform).
static Dictionary<string, V[]> FkWorld(
    Dictionary<string, (string parent, V pos, Q rot, V scale)> table,
    Dictionary<string, NodeAnimationChannel>? chan,
    Func<string, Q?> localOverride,
    IReadOnlyCollection<string> wanted, int frames, double tps, int fps)
{
    var order = new List<string>();
    {   // parents before children
        var pending = new HashSet<string>(table.Keys);
        while (pending.Count > 0)
        {
            bool any = false;
            foreach (var k in pending.ToList())
            {
                var par = table[k].parent;
                if (par == "" || !pending.Contains(par)) { order.Add(k); pending.Remove(k); any = true; }
            }
            if (!any) break;
        }
    }
    var result = wanted.ToDictionary(w => w, _ => new V[frames], StringComparer.Ordinal);
    var world = new Dictionary<string, Matrix4x4>(StringComparer.Ordinal);
    for (int f = 0; f < frames; f++)
    {
        double ticks = (double)f / fps * tps;
        world.Clear();
        foreach (var name in order)
        {
            var (parent, rp, rr, rs) = table[name];
            Q rot = rr; V pos = rp;
            if (chan != null && chan.TryGetValue(name, out var ch))
            {
                rot = SampleRot(ch, ticks);
                pos = SamplePos(ch, ticks, rp);
            }
            var ov = localOverride(name);
            if (ov is { } q) rot = q;
            var local = Matrix4x4.CreateScale(rs)
                      * Matrix4x4.CreateFromQuaternion(rot)
                      * Matrix4x4.CreateTranslation(pos);
            world[name] = (parent != "" && world.TryGetValue(parent, out var pw)) ? local * pw : local;
            if (result.ContainsKey(name)) result[name][f] = world[name].Translation;
        }
    }
    return result;
}

// ─── source side ─────────────────────────────────────────────────────────
var srcTable = BuildTable(scene.RootNode);
var srcChan = new Dictionary<string, NodeAnimationChannel>(StringComparer.Ordinal);
foreach (var c in anim.NodeAnimationChannels)
    if (!srcChan.ContainsKey(c.NodeName)) srcChan[c.NodeName] = c;

// Resolve source finger/hand node per GTA tag via the same name mapper.
var srcNameByTag = new Dictionary<ushort, string>();
foreach (var n in srcTable.Keys)
    if (GtaBoneTags.TryResolve(n, out var tag) && !srcNameByTag.ContainsKey(tag))
        srcNameByTag[tag] = n;

// ─── target side (freemode glb + emitted tracks) ─────────────────────────
var glbPath = Path.Combine(RuntimeAssets.ViewerDir, "reference", "freemode_male.glb");
var glb = ctx.ImportFile(glbPath, PostProcessSteps.None);
Node? Find(Node n, string name) { if (n.Name == name) return n; for (int i = 0; i < n.ChildCount; i++) { var f = Find(n.Children[i], name); if (f != null) return f; } return null; }
// Same rig the retarget itself used: GAME_RIG first, then SKEL_ROOT under it.
// The glb carries more than one skeleton and the display rig's locals differ.
var gameRig = Find(glb.RootNode, "GAME_RIG");
var gtaRoot = Find(gameRig ?? glb.RootNode, "SKEL_ROOT") ?? glb.RootNode;
var gtaTable = BuildTable(gtaRoot);
Console.WriteLine($"target rig root: {(gameRig != null ? "GAME_RIG/" : "")}{gtaRoot.Name}  nodes={gtaTable.Count}");
var gtaTagByName = new Dictionary<string, ushort>(StringComparer.Ordinal);
foreach (var n in gtaTable.Keys)
    if (GtaBoneTags.TryResolve(n, out var tag) && !gtaTagByName.ContainsValue(tag))
        gtaTagByName[n] = tag;
var gtaNameByTag = gtaTagByName.ToDictionary(kv => kv.Value, kv => kv.Key);

// Diagnostic: is every emitted finger track actually applicable to the FK rig?
{
    int fingerTracks = 0, applicable = 0;
    foreach (var t in tracks)
    {
        var gtaName = GtaBoneTags.ByGtaName.FirstOrDefault(kv => kv.Value == t.BoneTag).Key;
        if (gtaName is null || !gtaName.Contains("Finger", StringComparison.OrdinalIgnoreCase)) continue;
        fingerTracks++;
        if (gtaNameByTag.ContainsKey(t.BoneTag)) applicable++;
        else Console.WriteLine($"  UNAPPLIED finger track: tag={t.BoneTag} ({gtaName}) — no FK node resolved");
    }
    Console.WriteLine($"finger tracks: {fingerTracks} emitted, {applicable} applicable to FK rig");
    // Sample: does the track actually vary?
    if (GtaBoneTags.ByGtaName.TryGetValue("SKEL_L_Finger11", out var probe) && trackByTag.TryGetValue(probe, out var pf))
    {
        var q0 = pf[0]; var qm = pf[pf.Length / 2];
        Console.WriteLine($"probe SKEL_L_Finger11 q[0]=({q0.X:0.00},{q0.Y:0.00},{q0.Z:0.00},{q0.W:0.00}) q[mid]=({qm.X:0.00},{qm.Y:0.00},{qm.Z:0.00},{qm.W:0.00})");
    }
}

// ─── metric helpers ──────────────────────────────────────────────────────
static float Interior(V a, V b)
{
    if (a.LengthSquared() < 1e-12f || b.LengthSquared() < 1e-12f) return 0;
    var d = Math.Clamp(V.Dot(V.Normalize(a), V.Normalize(b)), -1f, 1f);
    return (float)Math.Acos(d);
}

// FLEXION about the knuckle line — the component the solver claims to
// transfer. Raw interior angle also contains abduction (finger spread), which
// the solver deliberately drops, so scoring on interior punished correct
// behaviour (left-hand MCPs read 0.0–0.3 while visually fine).
static float Flexion(V a, V b, V knuckleAxis)
{
    if (a.LengthSquared() < 1e-12f || b.LengthSquared() < 1e-12f || knuckleAxis.LengthSquared() < 1e-12f) return 0;
    a = V.Normalize(a); b = V.Normalize(b);
    var n = V.Normalize(knuckleAxis);
    return (float)Math.Atan2(V.Dot(V.Cross(a, b), n), V.Dot(a, b));
}

static double Corr(float[] x, float[] y)
{
    int n = Math.Min(x.Length, y.Length);
    if (n < 3) return 0;
    double mx = x.Take(n).Average(v => (double)v), my = y.Take(n).Average(v => (double)v);
    double sxy = 0, sxx = 0, syy = 0;
    for (int i = 0; i < n; i++) { double dx = x[i] - mx, dy = y[i] - my; sxy += dx * dy; sxx += dx * dx; syy += dy * dy; }
    return (sxx < 1e-9 || syy < 1e-9) ? 0 : sxy / Math.Sqrt(sxx * syy);
}

static float Deg(float rad) => rad * 180f / (float)Math.PI;

// Solver-intent curves: hand → digit → (mcp[], pip[]).
var intent = new Dictionary<string, Dictionary<int, (float[] mcp, float[] pip)>>(StringComparer.Ordinal);
if (File.Exists(intentCsv))
{
    foreach (var line in File.ReadLines(intentCsv))
    {
        var p = line.Split(',');
        if (p.Length < 5) continue;
        var hand = p[0]; int d = int.Parse(p[1]); int f = int.Parse(p[2]);
        if (!intent.TryGetValue(hand, out var per)) intent[hand] = per = new();
        if (!per.TryGetValue(d, out var cur)) per[d] = cur = (new float[frames], new float[frames]);
        if (f < frames)
        {
            cur.mcp[f] = float.Parse(p[3], System.Globalization.CultureInfo.InvariantCulture);
            cur.pip[f] = float.Parse(p[4], System.Globalization.CultureInfo.InvariantCulture);
        }
    }
    Console.WriteLine($"intent curves: {intent.Sum(kv => kv.Value.Count)} digits");
}

// ─── evaluate both hands ─────────────────────────────────────────────────
string[] digits = { "Thumb", "Index", "Middle", "Ring", "Pinky" };
double worstCorr = 1; int crossings = 0; int comparably = 0;

foreach (var side in new[] { "L", "R" })
{
    ushort handTag = GtaBoneTags.ByGtaName[$"SKEL_{side}_Hand"];
    if (!gtaNameByTag.TryGetValue(handTag, out var gtaHand) || !srcNameByTag.TryGetValue(handTag, out var srcHand))
    { Console.WriteLine($"{side}: hand unmapped — skip"); continue; }

    // Collect the chain node names per digit: [hand, j0, j1, j2].
    var wantSrc = new HashSet<string>(StringComparer.Ordinal) { srcHand };
    var wantGta = new HashSet<string>(StringComparer.Ordinal) { gtaHand };
    var chains = new List<(int d, string[] src, string[] gta)>();
    for (int d = 0; d < 5; d++)
    {
        var src = new string[3]; var gta = new string[3]; bool ok = true;
        for (int j = 0; j < 3 && ok; j++)
        {
            ok = GtaBoneTags.ByGtaName.TryGetValue($"SKEL_{side}_Finger{d}{j}", out var tag)
                 && srcNameByTag.TryGetValue(tag, out src[j]!)
                 && gtaNameByTag.TryGetValue(tag, out gta[j]!);
        }
        if (!ok) continue;
        chains.Add((d, src, gta));
        foreach (var s in src) wantSrc.Add(s);
        foreach (var g in gta) wantGta.Add(g);
    }
    if (chains.Count == 0) { Console.WriteLine($"{side}: no finger chains"); continue; }

    var srcPos = FkWorld(srcTable, srcChan, _ => null, wantSrc, frames, tps, fps);
    var gtaPos = FkWorldTracked(gtaTable, trackByTag, gtaTagByName, wantGta, frames);

    Console.WriteLine($"--- hand {side} ({chains.Count} digits) ---");
    // Target flexion about the per-frame knuckle line. The target FK is
    // self-consistent (the hand bone is forced to rest by the retarget), so
    // hand-referenced measurement is safe on THIS side.
    var idxC = chains.FirstOrDefault(c => c.d == 1);
    var pnkC = chains.FirstOrDefault(c => c.d == 4);
    bool haveAxis = idxC.src != null && pnkC.src != null;
    var tgtCurves = new Dictionary<int, (float[] mcp, float[] pip)>();
    foreach (var (d, _, gta) in chains)
    {
        var m = new float[frames]; var p = new float[frames];
        for (int f = 0; f < frames; f++)
        {
            var tAxis = haveAxis ? gtaPos[pnkC.gta[0]][f] - gtaPos[idxC.gta[0]][f] : V.UnitX;
            m[f] = Flexion(gtaPos[gta[0]][f] - gtaPos[gtaHand][f], gtaPos[gta[1]][f] - gtaPos[gta[0]][f], tAxis);
            p[f] = Flexion(gtaPos[gta[1]][f] - gtaPos[gta[0]][f], gtaPos[gta[2]][f] - gtaPos[gta[1]][f], tAxis);
        }
        tgtCurves[d] = (m, p);
    }

    if (!intent.TryGetValue(gtaHand, out var handIntent))
    { Console.WriteLine("no intent curves for this hand — solver skipped it"); continue; }

    // Sign alignment: the harness's axis convention vs the solver's
    // curl-positive convention differs per hand by at most a global flip.
    // Calibrate on the PIPs (known-clean measurement on both sides).
    double pipAgree = 0;
    foreach (var (d, _, _) in chains)
        if (d != 0 && handIntent.TryGetValue(d, out var ic) && tgtCurves.TryGetValue(d, out var tc))
            pipAgree += Corr(ic.pip, tc.pip);
    float flip = pipAgree < 0 ? -1f : 1f;

    Console.WriteLine("digit   joint  intent°     target°    corr    endOff°");
    foreach (var (d, _, _) in chains)
    {
        if (!handIntent.TryGetValue(d, out var ic) || !tgtCurves.TryGetValue(d, out var tc)) continue;
        void Report(string jn, float[] want, float[] got, bool scored)
        {
            var gotF = got.Select(v => v * flip).ToArray();
            double c = Corr(want, gotF);
            bool moves = want.Max() - want.Min() > 0.05f;
            if (scored && moves) { worstCorr = Math.Min(worstCorr, c); comparably++; }
            Console.WriteLine($"{digits[d],-7} {jn,-5}  {Deg(want.Min()),4:0}–{Deg(want.Max()),-4:0}  {Deg(gotF.Min()),4:0}–{Deg(gotF.Max()),-4:0}  {c,5:0.00}  {Deg(gotF[^1] - want[^1]),7:0.0}{(scored ? "" : "   (unscored)")}");
        }
        // Thumb is rest-anchored at MCP by design — report, don't score.
        Report("MCP", ic.mcp, tc.mcp, scored: d != 0);
        Report("PIP", ic.pip, tc.pip, scored: true);
    }

    // Crossing detector: fingertip order along the knuckle axis (index→pinky)
    // must keep its bind-frame sign for digits 1..4 every frame.
    var idx = chains.FirstOrDefault(c => c.d == 1); var pnk = chains.FirstOrDefault(c => c.d == 4);
    if (idx.gta != null && pnk.gta != null)
    {
        int bad = 0;
        for (int f = 0; f < frames; f++)
        {
            var axis = gtaPos[pnk.gta[0]][f] - gtaPos[idx.gta[0]][f];
            if (axis.LengthSquared() < 1e-12f) continue;
            axis = V.Normalize(axis);
            float prev = float.NegativeInfinity; bool okf = true;
            foreach (var c in chains.Where(c => c.d >= 1).OrderBy(c => c.d))
            {
                float proj = V.Dot(gtaPos[c.gta[2]][f] - gtaPos[idx.gta[0]][f], axis);
                if (proj < prev - 0.005f) { okf = false; break; }   // 5mm tolerance
                prev = proj;
            }
            if (!okf) bad++;
        }
        crossings += bad;
        Console.WriteLine($"crossing frames: {bad}/{frames}");
    }
}

Console.WriteLine();
Console.WriteLine($"SUMMARY worstCorr={worstCorr:0.00} on {comparably} moving joints, crossingFrames={crossings}");
Console.WriteLine(worstCorr >= 0.70 && crossings == 0 ? "VERDICT: PASS" : "VERDICT: FAIL");
return 0;

// Target FK with the emitted track quats swapped in per frame.
static Dictionary<string, V[]> FkWorldTracked(
    Dictionary<string, (string parent, V pos, Q rot, V scale)> table,
    Dictionary<ushort, Q[]> trackByTag,
    Dictionary<string, ushort> tagByName,
    IReadOnlyCollection<string> wanted, int frames)
{
    var order = new List<string>();
    var pending = new HashSet<string>(table.Keys);
    while (pending.Count > 0)
    {
        bool any = false;
        foreach (var k in pending.ToList())
        {
            var par = table[k].parent;
            if (par == "" || !pending.Contains(par)) { order.Add(k); pending.Remove(k); any = true; }
        }
        if (!any) break;
    }
    var result = wanted.ToDictionary(w => w, _ => new V[frames], StringComparer.Ordinal);
    var world = new Dictionary<string, Matrix4x4>(StringComparer.Ordinal);
    for (int f = 0; f < frames; f++)
    {
        world.Clear();
        foreach (var name in order)
        {
            var (parent, rp, rr, rs) = table[name];
            Q rot = rr;
            if (tagByName.TryGetValue(name, out var tag) && trackByTag.TryGetValue(tag, out var tr) && tr.Length > 0)
                rot = tr[Math.Min(f, tr.Length - 1)];
            var local = Matrix4x4.CreateScale(rs)
                      * Matrix4x4.CreateFromQuaternion(rot)
                      * Matrix4x4.CreateTranslation(rp);
            world[name] = (parent != "" && world.TryGetValue(parent, out var pw)) ? local * pw : local;
            if (result.ContainsKey(name)) result[name][f] = world[name].Translation;
        }
    }
    return result;
}
