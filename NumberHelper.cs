using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace RawThumbnailExtractor
{
    public static class NumberHelper
    {
        public static long HostToNetworkOrder(long host)
        {
            if (!BitConverter.IsLittleEndian)
                return host;

            return (((long)HostToNetworkOrder((int)host) & 0xFFFFFFFF) << 32)
                    | ((long)HostToNetworkOrder((int)(host >> 32)) & 0xFFFFFFFF);
        }

        public static int HostToNetworkOrder(int host)
        {
            if (!BitConverter.IsLittleEndian)
                return host;

            return (((int)HostToNetworkOrder((short)host) & 0xFFFF) << 16)
                    | ((int)HostToNetworkOrder((short)(host >> 16)) & 0xFFFF);
        }

        public static short HostToNetworkOrder(short host)
        {
            if (!BitConverter.IsLittleEndian)
                return host;

            return (short)((((int)host & 0xFF) << 8) | (int)((host >> 8) & 0xFF));
        }

        public static long NetworkToHostOrder(long network)
        {
            return HostToNetworkOrder(network);
        }

        public static int NetworkToHostOrder(int network)
        {
            return HostToNetworkOrder(network);
        }

        public static short NetworkToHostOrder(short network)
        {
            return HostToNetworkOrder(network);
        }
    }
}
