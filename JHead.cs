using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace DCRaw
{
    public class JHead
    {
        public int algo = 0;
        public int bits = 0;
        public int high = 0;
        public int wide = 0;
        public int clrs = 0;
        public int sraw = 0;
        public int psv = 0;
        public int restart = 0;
        public int[] vpred = new int[6];
        public ushort row;
        public ushort[] quant = new ushort[64];
        public ushort[] idct = new ushort[64];
        public ushort[] huff = new ushort[20];
        public ushort[] free = new ushort[20];
    }
}
