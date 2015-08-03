using System.Runtime.InteropServices;

namespace RawThumbnailExtractor
{
    [StructLayout(LayoutKind.Explicit, Pack = 1)]
    public struct TiffTag
    {
        [FieldOffset(0)]
        public ushort tag;

        [FieldOffset(2)]
        public ushort type;

        [FieldOffset(4)]
        public int count;

        [FieldOffset(8)]
        public int val;
    }
}
