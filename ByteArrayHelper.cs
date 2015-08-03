using System;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace DCRaw
{
    public static class ByteArrayHelper
    {
        public static byte[] SubArray(this byte[] array, int index)
        {
            if (index < array.Length)
            {
                var newArray = new byte[array.Length - index];

                for (int i = 0; i < newArray.Length; i++)
                    newArray[i] = array[index + i];

                return newArray;
            }
            return array;
        }

        public static int memcmp(this byte[] array, string comp, int count)
        {

            if (comp.Length < count)
                return 1;
            if (array.Length < count)
                return -1;
            int val = 0;
            for (int i = 0; i < count; i++)
            {
                val += array[i] - ((byte)comp[i]);
            }
            return val;
        }

        public static int strlen(this byte[] array)
        {
            if (array == null || array.Length == 0)
                return 0;

            var term = array.ToList().IndexOf(BitConverter.GetBytes('\0')[0]);

            if (term < 0)
                return 0;

            return term;
        }

        public static int strnlen(this byte[] array, int maxLen)
        {
            if (array == null || array.Length == 0)
                return 0;

            var term = array.ToList().IndexOf(BitConverter.GetBytes('\0')[0]);

            if (term < 0)
                return 0;

            if (term > maxLen)
                return maxLen;

            return term;
        }

        public static byte[] ToByteArray(this ushort[] sArray)
        {
            byte[] dest = new byte[sArray.Length * 2];
            Buffer.BlockCopy(sArray, 0, dest, 0, dest.Length);
            return dest;
        }

        public static byte[] ToByteArray(this short[] sArray)
        {
            byte[] dest = new byte[sArray.Length * 2];
            Buffer.BlockCopy(sArray, 0, dest, 0, dest.Length);
            return dest;
        }

        public static byte[] ToByteArray(this string str, int size = 0)
        {
            return Encoding.UTF8.GetBytes(str);
        }

        public static ushort[] ToUShortArray(this string str)
        {
            var bytes = ToByteArray(str);
            ushort[] dest = new ushort[(int)Math.Ceiling((double)bytes.Length / 2)];
            Buffer.BlockCopy(bytes, 0, dest, 0, bytes.Length);
            return dest;
        }

        public static void strncpy(this byte[] dest, byte[] source, int maxlen)
        {
            int i;

            for (i = 0; i < maxlen; i++)
            {
                if (i >= dest.Length || i >= source.Length)
                    break;

                if (source[i] == 0)
                    break;

                dest[i] = source[i];
            }

            // Set any remaining bytes on dest to 0 null byte
            for (var k = i; k < dest.Length; k++)
            {
                dest[k] = 0;
            }
        }

        public static string ToNullTerminatedString(this byte[] bytes)
        {
            var str = Encoding.UTF8.GetString(bytes, 0, bytes.Length);

            if (str.Length == 0)
                return string.Empty;

            var term = str.IndexOf('\0');
            if (term > 0)
                return str.Substring(0, term);
            else
                return str;
        }

        public static void memmove(this byte[] dest, byte[] source, int num)
        {
            for (var i = 0; i < num && i < source.Length && i < dest.Length; i++)
            {
                dest[i] = source[i];
            }
        }

        public static bool strcmp(this byte[] bytes, string val, int len = 0)
        {
            if (len == 0)
                len = val.Length;

            var byteStr = Encoding.UTF8.GetString(bytes, 0, bytes.Length);
            return val.Equals(byteStr.Substring(0, len), StringComparison.OrdinalIgnoreCase);
        }

        public static int strstr(this byte[] bytes, string val)
        {
            var byteStr = Encoding.UTF8.GetString(bytes, 0, bytes.Length);
            return byteStr.IndexOf(val, StringComparison.OrdinalIgnoreCase);
        }

        public static string getNumberString(this byte[] value)
        {
            var s = Encoding.UTF8.GetString(value, 0, value.Length);
            var match = Regex.Match(s, @"^\s*(\d+)");
            return match.Success ? match.Groups[1].Value : "0";
        }

        public static int atoi(this byte[] value)
        {
            return Int32.Parse(value.getNumberString());
        }

        public static float atof(this byte[] value)
        {
            return float.Parse(value.getNumberString());
        }
    }
}
