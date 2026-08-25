// Copyright © Neodymium, carmineos and contributors. See LICENSE.md in the repository root for more information.

using RageLib.Data;
using System;
using System.Numerics;

namespace RageLib.Resources.GTA4.PC.Bounds
{
    public struct PhBoundPrimitive : IResourceStruct<PhBoundPrimitive>
    {
        public Vector3 Normal;
        private uint MaterialAndArea;
        public short VertexIndex1;
        public short VertexIndex2;
        public short VertexIndex3;
        public short VertexIndex4;
        public short NeighborIndex1;
        public short NeighborIndex2;
        public short NeighborIndex3;
        public short NeighborIndex4;

        // MaterialAndArea packs the material index into the low byte and the
        // primitive's area into the top 24 bits of a float. Both setters were
        // broken: this one AND-assigned instead of writing the field (so any
        // material other than 0xFF silently destroyed the area), and Area below
        // dropped the material byte. Fixed to update only their own bits.
        public byte Material
        {
            get => (byte)(MaterialAndArea & 0xFF);
            set => MaterialAndArea = (MaterialAndArea & 0xFFFFFF00u) | value;   // FIVEOS PATCH
        }

        public float Area
        {
            get => BitConverter.UInt32BitsToSingle(MaterialAndArea & 0xFFFFFF00);
            // FIVEOS PATCH: preserve the material byte instead of zeroing it.
            set => MaterialAndArea = (BitConverter.SingleToUInt32Bits(value) & 0xFFFFFF00u)
                                     | (MaterialAndArea & 0xFFu);
        }

        public PhBoundPrimitive ReverseEndianness()
        {
            return new PhBoundPrimitive()
            {
                Normal = EndiannessExtensions.ReverseEndianness(Normal),
                MaterialAndArea = EndiannessExtensions.ReverseEndianness(MaterialAndArea),
                VertexIndex1 = EndiannessExtensions.ReverseEndianness(VertexIndex1),
                VertexIndex2 = EndiannessExtensions.ReverseEndianness(VertexIndex2),
                VertexIndex3 = EndiannessExtensions.ReverseEndianness(VertexIndex3),
                VertexIndex4 = EndiannessExtensions.ReverseEndianness(VertexIndex4),
                NeighborIndex1 = EndiannessExtensions.ReverseEndianness(NeighborIndex1),
                NeighborIndex2 = EndiannessExtensions.ReverseEndianness(NeighborIndex2),
                NeighborIndex3 = EndiannessExtensions.ReverseEndianness(NeighborIndex3),
                NeighborIndex4 = EndiannessExtensions.ReverseEndianness(NeighborIndex4),
            };
        }
    }
}
