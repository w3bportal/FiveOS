// Copyright © Neodymium, carmineos and contributors. See LICENSE.md in the repository root for more information.

using RageLib.Compression;
using RageLib.Data;
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;

namespace RageLib.Resources.GTA4
{
    public enum Resource5CompressionType : ushort
    {
        Deflate = 0xDA78,
    }

    public class Resource5 : IResource
    {
        protected const int RESOURCE_IDENT = 0x05435352;

        public int Version { get; set; }
        public uint Flags { get; set; }
        public Resource5CompressionType CompressionType { get; set; }

        public byte[] VirtualData { get; set; }
        public byte[] PhysicalData { get; set; }

        protected uint VirtualSize => (Flags & 0x7FF) << (int)(((Flags >> 11) & 0xF) + 8);
        protected uint PhysicalSize => ((Flags >> 15) & 0x7FF) << (int)(((Flags >> 26) & 0xF) + 8);

        public void Load(string fileName)
        {
            using (var fileStream = new FileStream(fileName, FileMode.Open, FileAccess.Read))
                Load(fileStream);
        }

        public virtual void Load(Stream stream)
        {
            var reader = new DataReader(stream);
            reader.Position = 0;

            // read the header
            var ident = reader.ReadUInt32();
            Version = reader.ReadInt32();
            Flags = reader.ReadUInt32();
            CompressionType = (Resource5CompressionType)reader.ReadUInt16();

            if (CompressionType != Resource5CompressionType.Deflate)
                throw new Exception($"Unsupported compression type {CompressionType}");

            uint virtualSize = VirtualSize;
            uint physicalSize = PhysicalSize;

            VirtualData = new byte[virtualSize];
            PhysicalData = new byte[physicalSize];

            var deflateStream = new DeflateStream(stream, CompressionMode.Decompress, true);
            deflateStream.ReadExactly(VirtualData, 0, (int)virtualSize);
            deflateStream.ReadExactly(PhysicalData, 0, (int)physicalSize);
            deflateStream.Close();
        }

        public void Save(string fileName)
        {
            using (var fileStream = new FileStream(fileName, FileMode.Create))
                Save(fileStream);
        }

        public virtual void Save(Stream stream)
        {
            var writer = new DataWriter(stream);

            // write the header
            writer.Write(RESOURCE_IDENT);
            writer.Write(Version);
            writer.Write(Flags);

            // FIVEOS PATCH: emit a genuine zlib stream.
            //
            // This used to hand-write CompressionType (0xDA78 -> the bytes
            // 78 DA) and then append RAW deflate, producing something that
            // merely looks like zlib from the front: no adler32 trailer, and
            // rejected by any real zlib reader. Every shipping Rockstar .wbn
            // and .wbd inflates cleanly as zlib from offset 12, so match them.
            //
            // ZLibStream writes those same two header bytes itself at level 9
            // (SmallestSize), so Load's CompressionType read still sees 78 DA
            // and the raw-deflate body still starts exactly where it expects.
            using (var zlibStream = new ZLibStream(stream, CompressionLevel.SmallestSize, leaveOpen: true))
            {
                zlibStream.Write(VirtualData, 0, VirtualData.Length);
                zlibStream.Write(PhysicalData, 0, PhysicalData.Length);
            }
        }

        public static bool IsResourceFile(string fileName)
        {
            using (var fileStream = new FileStream(fileName, FileMode.Open, FileAccess.Read))
                return IsResourceFile(fileStream);
        }

        public static bool IsResourceFile(Stream stream)
        {
            var reader = new DataReader(stream);
            var ident = reader.ReadInt32();
            stream.Position -= 4;
            return ident == RESOURCE_IDENT;
        }
    }

    public class Resource5<T> : Resource5, IResource<T> where T : PgBase32, new()
    {
        public T ResourceData { get; set; }

        public override void Load(Stream stream)
        {
            base.Load(stream);

            var systemStream = new MemoryStream(VirtualData);
            var graphicsStream = new MemoryStream(PhysicalData);
            var resourceStream = new ResourceDataReader(systemStream, graphicsStream);
            resourceStream.Position = 0x50000000;

            ResourceData = resourceStream.ReadBlock<T>();           
        }

        public override void Save(Stream stream)
        {
            ResourceHelpers.GetBlocks(ResourceData, out IList<IResourceBlock> systemBlocks, out IList<IResourceBlock> graphicBlocks);

            RemapBlocks(systemBlocks, graphicBlocks);

            ////////////////////////////////////////////////////////////////////////////
            // data to byte-array
            ////////////////////////////////////////////////////////////////////////////
            
            // TODO: Add a ResourceHeader struct 
            VirtualData = new byte[VirtualSize];
            PhysicalData = new byte[PhysicalSize];

            var virtualStream = new MemoryStream(VirtualData);
            var physicalStream = new MemoryStream(PhysicalData);
            var resourceWriter = new ResourceDataWriter(virtualStream, physicalStream);

            resourceWriter.Position = 0x50000000;
            foreach (var block in systemBlocks)
            {
                resourceWriter.Position = block.BlockPosition;

                var pos_before = resourceWriter.Position;
                block.Write(resourceWriter);
                var pos_after = resourceWriter.Position;

                if ((pos_after - pos_before) != block.BlockLength)
                {
                    // FIVEOS PATCH: name the offending block. A bare "error in
                    // system length" gives no way to tell which of a dozen
                    // block types declared a BlockLength its own Write does not
                    // match — and stale field-offset names make those easy to
                    // get wrong.
                    throw new Exception(
                        $"error in system length: {block.GetType().Name} declares BlockLength 0x{block.BlockLength:X} " +
                        $"but wrote 0x{pos_after - pos_before:X} bytes");
                }
            }

            resourceWriter.Position = 0x60000000;
            foreach (var block in graphicBlocks)
            {
                resourceWriter.Position = block.BlockPosition;

                var pos_before = resourceWriter.Position;
                block.Write(resourceWriter);
                var pos_after = resourceWriter.Position;

                if ((pos_after - pos_before) != block.BlockLength)
                {
                    throw new Exception("error in graphics length");
                }
            }

            virtualStream.Flush();
            virtualStream.Position = 0;

            physicalStream.Flush();
            physicalStream.Position = 0;

            base.Save(stream);
        }        

        public void RemapBlocks(IList<IResourceBlock> systemBlocks, IList<IResourceBlock> graphicBlocks)
        {
            var pgBase = ResourceData;

            // If we are building a new resource
            if(pgBase.PageMap is null)
            {
                // FIVEOS PATCH: give it its backing bytes. DatResourceMap32
                // declares a fixed 0x210 BlockLength but its Write just emits
                // Data, so a default-constructed one writes nothing and the
                // save aborts on a length mismatch. Zeros are fine — the loader
                // rebuilds the map from the page flags.
                pgBase.PageMap = new DatResourceMap32 { Data = new byte[0x210] };
                systemBlocks.Add(pgBase.PageMap);
            }

            // FIVEOS PATCH: implements the "TODO: Assign position to RSC5 blocks"
            // that shipped here unimplemented — without it every block keeps
            // BlockPosition 0, so the whole resource writes on top of itself.
            //
            // RSC5 paging is far simpler than GTA V's RSC7 nine-bucket scheme:
            // one uniform page size per segment, and blocks are allowed to
            // straddle page boundaries (a real 41 KB vertex array sits inside a
            // 16 KB-page file), so plain linear packing is correct — no bin
            // packing required.
            uint systemSize = AssignPositions(systemBlocks, 0x50000000);
            uint graphicsSize = AssignPositions(graphicBlocks, 0x60000000);
            Flags = EncodeFlags(systemSize, graphicsSize);

            static uint AssignPositions(IList<IResourceBlock> blocks, long segmentBase)
            {
                long offset = 0;
                foreach (var block in blocks)
                {
                    block.BlockPosition = segmentBase + offset;
                    offset += block.BlockLength;
                    // RAGE structures assume 16-byte alignment; unaligned
                    // pointers read as garbage on the console-derived layouts.
                    offset = (offset + 15L) & ~15L;
                }
                return (uint)offset;
            }
        }

        /// <summary>Pack two segment sizes into the RSC5 flags word.
        ///
        /// Layout, the inverse of <see cref="VirtualSize"/>/<see cref="PhysicalSize"/>:
        ///   bits  0-10  system page count      bits 11-14  system page shift
        ///   bits 15-25  graphics page count    bits 26-29  graphics page shift
        /// with page size = 1 &lt;&lt; (shift + 8). The top two bits are set on every
        /// shipping file (checked against Rockstar's int_door.wbd = 0xC000201D,
        /// which decodes to 29 pages x 4096 = 118,784 bytes).
        ///
        /// 4096-byte pages are the default because that is what Rockstar's own
        /// bounds use; larger pages are only taken when a segment would need
        /// more than the 2047 pages the count field can hold.</summary>
        private static uint EncodeFlags(uint systemSize, uint graphicsSize)
        {
            var (sysCount, sysShift) = Pack(systemSize);
            var (gfxCount, gfxShift) = Pack(graphicsSize);
            return 0xC0000000u
                 | (sysCount & 0x7FFu)
                 | ((sysShift & 0xFu) << 11)
                 | ((gfxCount & 0x7FFu) << 15)
                 | ((gfxShift & 0xFu) << 26);

            static (uint Count, uint Shift) Pack(uint size)
            {
                if (size == 0) return (0u, 0u);
                for (uint shift = 4; shift <= 0xF; shift++)
                {
                    uint pageSize = 1u << (int)(shift + 8);
                    uint count = (size + pageSize - 1) / pageSize;
                    if (count <= 0x7FF) return (count, shift);
                }
                throw new Exception($"resource segment of {size} bytes is too large for an RSC5 page map");
            }
        }

        public void Rebuild()
        {
            ResourceHelpers.RebuildBlocks(ResourceData);
        }
    }
}