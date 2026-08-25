// Copyright (c) 2026 FiveOS. All rights reserved.
// https://github.com/w3bportal/FiveOS

using RageLib.Resources;
using RageLib.Resources.GTA4.PC.Bounds;

namespace YdrWriter.GtaIv;

/// <summary>The root block of a GTA IV bounds resource — RAGE's
/// <c>datOwner&lt;phBound&gt;</c>.
///
/// A .wbn is not a bare phBound: the resource's root has to derive from pgBase
/// so the loader finds the page map, and it holds the actual bound behind a
/// pointer. The vendored rage-toolkit models every bound type but not this
/// wrapper, so without it there is nothing a <c>Resource5&lt;T&gt;</c> can be
/// parameterised on.
///
/// Layout (32-bit, matching a shipping .wbn):
///   +0x00  vtable            (DatBase32)
///   +0x04  page map pointer  (PgBase32)
///   +0x08  bound pointer
///   +0x0C  padding
/// </summary>
public class IvBoundOwner : PgBase32
{
    public override long BlockLength => 0x10;

    private uint BoundPointer;
    private uint Unknown_0Ch;

    public PhBound? Bound { get; set; }

    public override void Read(ResourceDataReader reader, params object[] parameters)
    {
        base.Read(reader, parameters);
        BoundPointer = reader.ReadUInt32();
        Unknown_0Ch = reader.ReadUInt32();
        Bound = reader.ReadBlockAt<PhBound>(BoundPointer);
    }

    public override void Write(ResourceDataWriter writer, params object[] parameters)
    {
        base.Write(writer, parameters);
        BoundPointer = (uint)(Bound?.BlockPosition ?? 0);
        writer.Write(BoundPointer);
        writer.Write(Unknown_0Ch);
    }

    public override IResourceBlock[] GetReferences()
    {
        var list = new List<IResourceBlock>(base.GetReferences());
        if (Bound is not null) list.Add(Bound);
        return list.ToArray();
    }
}
