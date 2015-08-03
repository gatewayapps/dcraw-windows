using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;

namespace DCRaw
{
    [StructLayout(LayoutKind.Explicit, Pack = 1)]
    public class TiffHdr
    {
        [FieldOffset(0)]
        public ushort order;

        [FieldOffset(2)]
        public ushort magic;

        [FieldOffset(4)]
        public int ifd;

        [FieldOffset(8)]
        public ushort pad;

        [FieldOffset(10)]
        public ushort ntag;

        [FieldOffset(12)]
        [MarshalAs(UnmanagedType.ByValArray, ArraySubType = UnmanagedType.Struct, SizeConst = 23)]
        public TiffTag[] tag = new TiffTag[23];

        [FieldOffset(288)]
        public int nextifd;

        [FieldOffset(292)]
        public ushort pad2;

        [FieldOffset(294)]
        public ushort nexif;

        [FieldOffset(296)]
        [MarshalAs(UnmanagedType.ByValArray, ArraySubType = UnmanagedType.Struct, SizeConst = 7)]
        public TiffTag[] exif = new TiffTag[7];

        [FieldOffset(380)]
        public ushort pad3;

        [FieldOffset(382)]
        public ushort ngps;

        [FieldOffset(384)]
        [MarshalAs(UnmanagedType.ByValArray, ArraySubType = UnmanagedType.Struct, SizeConst = 10)]
        public TiffTag[] gpst = new TiffTag[10];

        [FieldOffset(504)]
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 4)]
        public short[] bps = new short[4];

        [FieldOffset(512)]
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 12)]
        public int[] rat = new int[12];

        [FieldOffset(560)]
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 26)]
        public uint[] gps = new uint[26];

        [FieldOffset(664)]
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 512)]
        public byte[] desc = new byte[512];

        [FieldOffset(1176)]
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 64)]
        public byte[] make = new byte[64];

        [FieldOffset(1240)]
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 64)]
        public byte[] model = new byte[64];

        [FieldOffset(1304)]
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)]
        public byte[] soft = new byte[32];

        [FieldOffset(1336)]
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 20)]
        public byte[] date = new byte[20];

        [FieldOffset(1356)]
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 64)]
        public byte[] artist = new byte[64];
    }
}
