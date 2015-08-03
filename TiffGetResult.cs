using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace DCRaw
{
    public struct TiffGetResult
    {
        public TiffGetResult(uint tag, uint type, uint len, uint save)
            : this()
        {
            Tag = tag;
            Type = type;
            Len = len;
            Save = save;
        }

        public uint Tag { get; private set; }
        public uint Type { get; private set; }
        public uint Len { get; private set; }
        public uint Save { get; private set; }
    }
}
