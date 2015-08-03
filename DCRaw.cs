using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Windows.Storage.Streams;
using Windows.UI.Xaml.Media.Imaging;

namespace DCRaw
{
    public class DCRaw : IDisposable
    {
        private Stream ifp = null;
        private MemoryStream outStream = null;
        private ushort raw_height, raw_width, height, width;
        private short order;
        private UInt32 tile_width, tile_length, load_flags;
        private UInt32 tiff_nifds, tiff_samples, tiff_bps, tiff_compress;
        private UInt32 thumb_offset, data_offset, meta_offset;
        private byte[] make = new byte[64];
        private byte[] model = new byte[64];
        private UInt32 filters;
        private UInt32 tiff_flip;
        private UInt32 thumb_length = 0;

        private byte[] model2 = new byte[64];
        private uint shot_order, meta_length;
        private uint unique_id;
        private int is_raw = 1;
        private uint flip, shot_select = 0, fuji_layout, fuji_width;
        private byte[] desc = new byte[512], artist = new byte[64];
        private uint dng_version = 0;
        private int thumb_width, thumb_height, thumb_misc;
        private List<TiffIfd> tiff_ifd_c = new List<TiffIfd>();
        private float shutter, aperture, focal_len, iso_speed, flash_used, exposure;
        private ushort flash, exposure_program;
        private DateTimeOffset timestamp;

        private Func<Task<MemoryStream>> write_thumb;

        private static string[] corp = new string[21]
        {
            "AgfaPhoto", "Canon", "Casio", "Epson", "Fujifilm",
		    "Mamiya", "Minolta", "Motorola", "Kodak", "Konica", "Leica",
		    "Nikon", "Nokia", "Olympus", "Pentax", "Phase One", "Ricoh",
		    "Samsung", "Sigma", "Sinar", "Sony"
        };

        public DCRaw(Stream inStream)
        {
            ifp = inStream;
            write_thumb = WriteJpegThumb;
        }

        public async Task<byte[]> GetThumbnailBytesAsync()
        {
            var stream = await GetThumbnailStreamAsync();
            if (stream == null)
                return null;

            return stream.ToArray();
        }

        public async Task<MemoryStream> GetThumbnailStreamAsync()
        {
            await Identify();

            if (thumb_offset <= 0 || write_thumb == null)
                return null;

            ifp.Seek(thumb_offset, SeekOrigin.Begin);

            return await write_thumb();
        }

        public async Task<BitmapImage> GetThumbnailImageAsync()
        {
            var stream = await GetThumbnailStreamAsync();
            if (stream == null)
                return null;

            var bmp = new BitmapImage();
            await bmp.SetSourceAsync(stream.AsRandomAccessStream());

            return bmp;
        }

        public void Dispose()
        {
            System.Diagnostics.Debug.WriteLine("Disposing RawThumbnailExtractor");
            ifp = null;
            if (outStream != null)
                outStream.Dispose();
        }

        #region Raw Parsing

        private async Task Identify()
        {
            System.Diagnostics.Debug.WriteLine("Begin Identify");
            int hlen, flen, fsize, i;
            byte[] head = new byte[32];

            flip = UInt32.MaxValue;
            tiff_flip = UInt32.MaxValue;

            tiff_nifds = 0;
            order = (short)(await Get2Async());
            hlen = (int)(await Get4Async());
            ifp.Seek(0, SeekOrigin.Begin);
            await ifp.ReadAsync(head, 0, 32);
            ifp.Seek(0, SeekOrigin.End);
            flen = fsize = (int)ifp.Position;

            if (order == 0x4949 || order == 0x4d4d)
            {
                if (head.SubArray(6).strcmp("HEAPCCDR", 8))
                {
                    data_offset = (uint)hlen;
                    await ParseCiff(hlen, flen - hlen, 0);
                }
                await ParseTiffAsync(0);
                await ApplyTiff();
            }
            else if (head.memcmp("\xff\xd8\xff\xe1", 4) == 0 &&
                head.SubArray(6).memcmp("Exif", 4) == 0)
            {
                ifp.Seek(4, SeekOrigin.Begin);
                data_offset = await Get2Async() + (uint)4;
                ifp.Seek(data_offset, SeekOrigin.Begin);

                if (ifp.ReadByte() != 0xff)
                    await ParseTiffAsync(12);

                thumb_offset = 0;
            }
            else if (head.strcmp("FUJIFILM", 8))
            {
                ifp.Seek(84, SeekOrigin.Begin);
                thumb_offset = await Get4Async();
                thumb_length = await Get4Async();
                ifp.Seek(92, SeekOrigin.Begin);
                await ParseFuji((int)(await Get4Async()));
                if (thumb_offset > 120)
                {
                    ifp.Seek(120, SeekOrigin.Begin);
                    is_raw += (i = (int)await Get4Async()) > 0 ? 1 : 0;
                    if (is_raw == 2 && shot_select > 0)
                        await ParseFuji(i);
                }
                var newPos = 100 + 28 * ((shot_select > 0) ? 1 : 0);
                ifp.Seek(newPos, SeekOrigin.Begin);
                await ParseTiffAsync((int)(data_offset = await Get4Async()));
                await ParseTiffAsync((int)thumb_offset + 12);
                await ApplyTiff();
            }
            else if (head.strcmp("\0MRM", 4))
            {
                await ParseMinolta(0);
            }
            else if (head.strcmp("FOVb", 4))
            {
                await ParseFoveon();
            }

            if (make[0] == 0)
            {
                await ParseJpegAsync(0);
            }

            /* Simplify company names */
            for (i = 0; i < corp.Length; i++)
            {
                if (make.strcmp(corp[i]))
                    make.strncpy(corp[i].ToByteArray(), 64);
            }

            /* Remove make from model */
            i = make.strlen();
            var makeStr = make.ToNullTerminatedString();
            if (model.strcmp(makeStr) && model[i++] == ' ')
            {
                model.memmove(model.SubArray(i), 64 - i);
            }

            if (flip == UInt32.MaxValue)
                flip = tiff_flip;

            if (flip == UInt32.MaxValue)
                flip = 0;

            switch ((flip + 3600) % 360)
            {
                case 270:
                    flip = 5;
                    break;
                case 180:
                    flip = 3;
                    break;
                case 90:
                    flip = 6;
                    break;
            }
        }

        private async Task ParseCiff(int offset, int length, int depth)
        {
            System.Diagnostics.Debug.WriteLine("Begin Parse Ciff");
            int tboff, nrecs, type, len, save, wbi = -1;

            ifp.Seek(offset + length - 4, SeekOrigin.Begin);
            tboff = (int)await Get4Async() + offset;
            ifp.Seek(tboff, SeekOrigin.Begin);
            nrecs = await Get2Async();

            if ((nrecs | depth) > 127)
                return;

            while (nrecs-- > 0)
            {
                type = await Get2Async();
                len = (int)await Get4Async();
                save = (int)ifp.Position + 4;

                ifp.Seek(offset + await Get4Async(), SeekOrigin.Begin);

                if ((((type >> 8) + 8) | 8) == 0x38)
                {
                    await ParseCiff((int)ifp.Position, len, depth + 1);  /* Parse sub-table */
                }
                if (type == 0x0810)
                {
                    await ifp.ReadAsync(artist, 0, 64);
                }
                if (type == 0x080a)
                {
                    await ifp.ReadAsync(make, 0, 64);
                    ifp.Seek(make.strlen() - 63, SeekOrigin.Current);
                    await ifp.ReadAsync(model, 0, 64);
                }
                if (type == 0x1810)
                {
                    width = (ushort)await Get4Async();
                    height = (ushort)await Get4Async();
                    await Get4Async();
                    flip = await Get4Async();
                }
                if (type == 0x1835)
                {
                    tiff_compress = await Get4Async();
                }
                if (type == 0x2007)
                {
                    thumb_offset = (uint)ifp.Position;
                    thumb_length = (uint)len;
                }
                if (type == 0x1818)
                {
                    await Get4Async();
                    shutter = (float)Math.Pow(2, -IntToFloat((int)await Get4Async()));
                    aperture = (float)Math.Pow(2, IntToFloat((int)await Get4Async()) / 2);
                }
                if (type == 0x102a)
                {
                    await Get4Async();
                    iso_speed = (float)Math.Pow(2, await Get2Async() / 32.0 - 4) * 50;

                    await Get2Async();
                    aperture = (float)Math.Pow(2, await Get2Async() / 64.0);

                    shutter = (float)Math.Pow(2, -(await Get2Async()) / 32.0);

                    await Get2Async();
                    wbi = await Get2Async();

                    if (wbi > 17)
                    {
                        wbi = 0;
                    }

                    ifp.Seek(32, SeekOrigin.Current);

                    if (shutter > 1e6)
                    {
                        shutter = (float)(await Get2Async() / 10.0);
                    }
                }
                if (type == 0x1031)
                {
                    await Get2Async();
                    raw_width = await Get2Async();
                    raw_height = await Get2Async();
                }
                if (type == 0x5029)
                {
                    focal_len = len >> 16;
                    if ((len & 0xffff) == 2)
                        focal_len /= 32;
                }
                if (type == 0x5813)
                {
                    flash_used = IntToFloat(len);
                }
                if (type == 0x5817)
                {
                    shot_order = (uint)len;
                }
                if (type == 0x5834)
                {
                    unique_id = (uint)len;
                }
                if (type == 0x580e)
                {
                    timestamp = new DateTimeOffset(len.FromUnixTime());
                }
                if (type == 0x180e)
                {
                    timestamp = new DateTimeOffset(((int)await Get4Async()).FromUnixTime());
                }

                ifp.Seek(save, SeekOrigin.Begin);
            }
        }

        private async Task ParseMos(int offset)
        {
            System.Diagnostics.Debug.WriteLine("Begin Parse Mos");
            byte[] data = new byte[40];
            int skip, from;

            string[] mod = new string[39] { 
                "", "DCB2", "Volare", "Cantare", "CMost", "Valeo 6", "Valeo 11", "Valeo 22",
	            "Valeo 11p", "Valeo 17", "", "Aptus 17", "Aptus 22", "Aptus 75", "Aptus 65",
	            "Aptus 54S", "Aptus 65S", "Aptus 75S", "AFi 5", "AFi 6", "AFi 7",
	            "AFi-II 7", "Aptus-II 7", "", "Aptus-II 6", "", "", "Aptus-II 10", "Aptus-II 5",
	            "", "", "", "", "Aptus-II 10R", "Aptus-II 8", "", "Aptus-II 12", "", "AFi-II 12"
            };

            ifp.Seek(offset, SeekOrigin.Begin);

            while (true)
            {
                if (await Get4Async() != 0x504b5453)
                    break;

                await Get4Async();

                await ifp.ReadAsync(data, 0, 40);

                skip = (int)await Get4Async();
                from = (int)ifp.Position;

                if (data.strcmp("JPEG_preview_data"))
                {
                    thumb_offset = (uint)from;
                    thumb_length = (uint)skip;
                }
                if (data.strcmp("ShootObj_back_type"))
                {
                    var data2 = new byte[10];
                    await ifp.ReadAsync(data2, 0, 10);
                    var str = data2.getNumberString();
                    if (!String.IsNullOrWhiteSpace(str))
                    {
                        var i = Int32.Parse(str);
                        if (i < mod.Length)
                            model.strncpy(mod[i].ToByteArray(), 64);
                    }
                }

                await ParseMos(from);
                ifp.Seek(skip + from, SeekOrigin.Begin);
            }
        }

        private async Task ParseFuji(int offset)
        {
            System.Diagnostics.Debug.WriteLine("Begin Parse Fuji");
            uint entries, tag, len, save;

            ifp.Seek(offset, SeekOrigin.Begin);

            entries = await Get4Async();

            if (entries > 255)
                return;

            while (entries-- > 0)
            {
                tag = await Get2Async();
                len = await Get2Async();
                save = (uint)ifp.Position;

                if (tag == 0x100)
                {
                    raw_height = await Get2Async();
                    raw_width = await Get2Async();
                }
                else if (tag == 0x130)
                {
                    fuji_layout = (uint)ifp.ReadByte() >> 7;
                    fuji_width = !((ifp.ReadByte() & 8) > 0) ? (uint)1 : (uint)0;
                }
                else if (tag == 0x121)
                {
                    height = await Get2Async();
                    if ((width = await Get2Async()) == 4284)
                        width += 3;
                }

                ifp.Seek(save + len, SeekOrigin.Begin);
            }

            height <<= (ushort)fuji_layout;
            width >>= (ushort)fuji_layout;
        }

        private async Task ParseMinolta(int base_c)
        {
            System.Diagnostics.Debug.WriteLine("Begin Parse Minolta");
            int save, tag, len, offset, high = 0, wide = 0, i;
            short sorder = order;

            ifp.Seek(base_c, SeekOrigin.Begin);

            if (ifp.ReadByte() > 0 || ifp.ReadByte() - 'M' > 0 || ifp.ReadByte() - 'R' > 0)
                return;

            order = (short)(ifp.ReadByte() * 0x101);
            offset = base_c + (int)await Get4Async() + 8;

            while ((save = (int)ifp.Position) < offset)
            {
                for (tag = i = 0; i < 4; i++)
                    tag = tag << 8 | ifp.ReadByte();

                len = (int)await Get4Async();

                switch (tag)
                {
                    case 0x505244:
                        ifp.Seek(8, SeekOrigin.Current);
                        high = await Get2Async();
                        wide = await Get2Async();
                        break;
                    case 0x545457:
                        await ParseTiffAsync((int)ifp.Position);
                        data_offset = (uint)offset;
                        break;
                }

                ifp.Seek(save + len + 8, SeekOrigin.Begin);
            }

            raw_height = (ushort)high;
            raw_width = (ushort)wide;
            order = sorder;
        }

        private async Task ParseFoveon()
        {
            System.Diagnostics.Debug.WriteLine("Begin Parse Foveon");
            int entries, img = 0, off, len, tag, save, wide, high, pent, i;
            byte[] name = new byte[64], value = new byte[64];
            int[,] poff = new int[256, 2];

            order = 0x4949; /* Little-endian */

            ifp.Seek(36, SeekOrigin.Begin);

            flip = await Get4Async();

            ifp.Seek(-4, SeekOrigin.End);
            ifp.Seek(await Get4Async(), SeekOrigin.Begin);

            if (await Get4Async() != 0x64434553) /* SECd */
                return;

            await Get4Async();
            entries = (int)await Get4Async();

            while (entries-- > 0)
            {
                off = (int)await Get4Async();
                len = (int)await Get4Async();
                tag = (int)await Get4Async();
                save = (int)ifp.Position;

                ifp.Seek(off, SeekOrigin.Begin);

                if (await Get4Async() != (0x20434553 | (tag << 24)))
                    return;

                switch (tag)
                {
                    case 0x47414d49:			/* IMAG */
                    case 0x32414d49:			/* IMA2 */
                        ifp.Seek(8, SeekOrigin.Current);
                        pent = (int)await Get4Async();
                        wide = (int)await Get4Async();
                        high = (int)await Get4Async();
                        if (wide > raw_width && high > raw_height)
                        {
                            raw_width = (ushort)wide;
                            raw_height = (ushort)high;
                            data_offset = (uint)off + 28;
                        }
                        ifp.Seek(off + 28, SeekOrigin.Begin);
                        if (ifp.ReadByte() == 0xff && ifp.ReadByte() == 0xd8
                            && thumb_length < len - 28)
                        {
                            thumb_offset = (uint)off + 28;
                            thumb_length = (uint)len - 28;
                            write_thumb = WriteJpegThumb;
                        }
                        if (++img == 2 && thumb_length == 0)
                        {
                            thumb_offset = (uint)off + 24;
                            thumb_width = wide;
                            thumb_height = high;
                            write_thumb = WriteFoveonThumb;
                        }
                        break;
                    case 0x464d4143:			/* CAMF */
                        meta_offset = (uint)off + 8;
                        meta_length = (uint)len - 28;
                        break;
                    case 0x504f5250:			/* PROP */
                        await Get4Async();
                        pent = (int)await Get4Async();

                        ifp.Seek(12, SeekOrigin.Current);

                        off += pent * 8 + 24;

                        if (pent > 256)
                            pent = 256;

                        for (i = 0; i < pent * 2; i++)
                        {
                            poff[i, 0] = off + (int)await Get4Async() * 2;
                        }

                        for (i = 0; i < pent; i++)
                        {
                            await FoveonGetsAsync(poff[i, 0], name, 64);
                            await FoveonGetsAsync(poff[i, 1], value, 64);

                            if (name.strcmp("ISO"))
                            {
                                iso_speed = value.atoi();
                            }
                            if (name.strcmp("CAMMANUF"))
                            {
                                make = value;
                            }
                            if (name.strcmp("CAMMODEL"))
                            {
                                model = value;
                            }
                            if (name.strcmp("TIME"))
                            {
                                timestamp = new DateTimeOffset(value.atoi().FromUnixTime());
                            }
                            if (name.strcmp("EXPTIME"))
                            {
                                shutter = (float)(value.atoi() / 1000000.0);
                            }
                            if (name.strcmp("APERTURE"))
                            {
                                aperture = value.atof();
                            }
                            if (name.strcmp("FLENGTH"))
                            {
                                focal_len = value.atof();
                            }
                        }

                        break;
                }

                ifp.Seek(save, SeekOrigin.Begin);
            }
        }

        private async Task<byte[]> FoveonGetsAsync(int offset, byte[] str, int len)
        {
            int i;
            ifp.Seek(offset, SeekOrigin.Begin);
            for (i = 0; i < len - 1; i++)
            {
                if ((str[i] = (byte)await Get2Async()) == 0)
                    break;
            }
            str[i] = 0;
            return str;
        }

        private async Task<int> ParseJpegAsync(int offset)
        {
            System.Diagnostics.Debug.WriteLine("Begin Parse Jpeg");
            int len, save, hlen, mark;

            ifp.Seek(offset, SeekOrigin.Begin);

            if (ifp.ReadByte() != 0xff || ifp.ReadByte() != 0xd8)
                return 0;

            while (ifp.ReadByte() == 0xff && (mark = ifp.ReadByte()) != 0xda)
            {
                order = 0x4d4d;
                len = await Get2Async() - 2;
                save = (int)ifp.Position;

                if (mark == 0xc0 || mark == 0xc3)
                {
                    ifp.ReadByte();
                    raw_height = await Get2Async();
                    raw_width = await Get2Async();
                }

                order = (short)await Get2Async();
                hlen = (int)await Get4Async();

                if (await Get4Async() == 0x48454150)  /* "HEAP" */
                    await ParseCiff(save + hlen, len - hlen, 0);

                if (await ParseTiffAsync(save + 6) > 0)
                    await ApplyTiff();

                ifp.Seek(save + len, SeekOrigin.Begin);
            }

            return 1;
        }

        private async Task<int> ParseTiffAsync(int base_c)
        {
            System.Diagnostics.Debug.WriteLine("Begin Parse Tiff");
            int doff;
            ifp.Seek(base_c, SeekOrigin.Begin);

            order = (short)(await Get2Async());

            if (order != 0x4949 && order != 0x4d4d)
                return 0;

            await Get2Async();

            while ((doff = (int)await Get4Async()) > 0)
            {
                ifp.Seek(doff + base_c, SeekOrigin.Begin);
                if (await ParseTiffIfdAsync(base_c) > 0)
                    break;
            }
            return 1;
        }

        private async Task<int> ParseTiffIfdAsync(int base_c)
        {
            System.Diagnostics.Debug.WriteLine("Begin Parse Tiff Ifd");
            tiff_ifd_c.Add(new TiffIfd());
            UInt32 entries = 0, tag = 0, type = 0, len = 0, save = 0;
            int ifd, i = 0;
            JHead jh = new JHead();

            byte[] software = new byte[64];
            ifd = (int)tiff_nifds++;
            entries = await Get2Async();

            if (entries > 512)
                return 1;

            while (entries-- > 0)
            {
                var result = await TiffGet((UInt32)base_c);
                tag = result.Tag;
                type = result.Type;
                len = result.Len;
                save = result.Save;

                switch (tag)
                {
                    case 5:
                        width = await Get2Async();
                        break;
                    case 6:
                        height = await Get2Async();
                        break;
                    case 7:
                        width += await Get2Async();
                        break;
                    case 9:
                        filters = await Get2Async();
                        break;
                    case 23:
                        if (type == 3)
                            iso_speed = (float)await Get2Async();
                        break;
                    case 46:
                        if (type != 7 || ifp.ReadByte() != 0xff || ifp.ReadByte() != 0xd8) break;
                        thumb_offset = (uint)(ifp.Position - 2);
                        thumb_length = len;
                        break;
                    case 61440:  /* Fuji HS10 table */
                        ifp.Seek(await Get4Async() + base_c, SeekOrigin.Begin);
                        await ParseTiffIfdAsync(base_c);
                        break;
                    case 2:
                    case 256:
                    case 61441:
                        tiff_ifd_c[ifd].width = (int)await GetIntAsync((int)type);
                        break;
                    case 3:
                    case 257:
                    case 61442:
                        tiff_ifd_c[ifd].height = (int)await GetIntAsync((int)type);
                        break;
                    case 258:				/* BitsPerSample */
                    case 61443:
                        tiff_ifd_c[ifd].samples = (int)(len & 7);
                        tiff_ifd_c[ifd].bps = (int)await GetIntAsync((int)type);
                        break;
                    case 61446:
                        raw_height = 0;
                        if (tiff_ifd_c[ifd].bps > 12)
                            break;
                        load_flags = (uint)(await Get4Async() > 0 ? 24 : 80);
                        break;
                    case 259:				/* Compression */
                        tiff_ifd_c[ifd].comp = (int)await GetIntAsync((int)type);
                        break;
                    case 262:				/* PhotometricInterpretation */
                        tiff_ifd_c[ifd].phint = await Get2Async();
                        break;
                    case 270:				/* ImageDescription */
                        await ifp.ReadAsync(desc, 0, 512);
                        break;
                    case 271:
                        await ifp.ReadAsync(make, 0, 64);
                        break;
                    case 272:
                        await ifp.ReadAsync(model, 0, 64);
                        break;
                    case 280:				/* Panasonic RW2 offset */
                        if (type != 4)
                            break;
                        load_flags = 0x2008;
                        goto case 61447;
                    case 273:
                    case 513:
                    case 61447:
                        tiff_ifd_c[ifd].offset = (int)(await Get4Async() + base_c);
                        if (tiff_ifd_c[ifd].bps == 0 && tiff_ifd_c[ifd].offset > 0)
                        {
                            ifp.Seek(tiff_ifd_c[ifd].offset, SeekOrigin.Begin);
                            if (await LJpegStartAsync(jh, 1) > 0)
                            {
                                tiff_ifd_c[ifd].comp = 6;
                                tiff_ifd_c[ifd].width = jh.wide;
                                tiff_ifd_c[ifd].height = jh.high;
                                tiff_ifd_c[ifd].bps = jh.bits;
                                tiff_ifd_c[ifd].samples = jh.clrs;
                                if (!(jh.sraw > 0 || (jh.clrs & 1) > 0))
                                    tiff_ifd_c[ifd].width *= jh.clrs;
                                if ((tiff_ifd_c[ifd].width > 4 * tiff_ifd_c[ifd].height) && jh.clrs > 0)
                                {
                                    tiff_ifd_c[ifd].width /= 2;
                                    tiff_ifd_c[ifd].height *= 2;
                                }
                                i = order;
                                await ParseTiffAsync(tiff_ifd_c[ifd].offset + 12);
                                order = (short)i;
                            }
                        }
                        break;
                    case 274:
                        tiff_ifd_c[ifd].flip = "50132467"[await Get2Async() & 7] - '0';
                        break;
                    case 277:				/* SamplesPerPixel */
                        tiff_ifd_c[ifd].samples = (int)await GetIntAsync((int)type) & 7;
                        break;
                    case 279:
                    case 514:
                    case 61448:
                        tiff_ifd_c[ifd].bytes = (int)await Get4Async();
                        break;
                    case 305:
                    case 11:
                        await ifp.ReadAsync(software, 0, 64);
                        break;
                    case 306:				/* DateTime */
                        GetTimestamp(0);
                        break;
                    case 315:				/* artist */
                        await ifp.ReadAsync(artist, 0, 64);
                        break;
                    case 322:				/* TileWidth */
                        tiff_ifd_c[ifd].tile_width = (int)await GetIntAsync((int)type);
                        break;
                    case 323:				/* TileLength */
                        tiff_ifd_c[ifd].tile_length = (int)await GetIntAsync((int)type);
                        break;
                    case 324:				/* TileOffsets */
                        tiff_ifd_c[ifd].offset = (int)(len > 1 ? ifp.Position : await Get4Async());
                        if (len == 1)
                            tiff_ifd_c[ifd].tile_width = tiff_ifd_c[ifd].tile_length = 0;
                        if (len == 4)
                        {
                            is_raw = 5;
                        }
                        break;
                    case 330:				/* SubIFDs */
                        if (!model.strcmp("DSLR-A100") && tiff_ifd_c[ifd].width == 3872)
                        {
                            data_offset = (uint)(await Get4Async() + base_c);
                            ifd++;
                            break;
                        }
                        while (len-- > 0)
                        {
                            i = (int)ifp.Position;
                            ifp.Seek(await Get4Async() + base_c, SeekOrigin.Begin);
                            if (await ParseTiffIfdAsync(base_c) > 0)
                                break;
                            ifp.Seek(i + 4, SeekOrigin.Begin);
                        }
                        break;
                    case 33434:
                        tiff_ifd_c[ifd].shutter = shutter = (float)await GetRealAsync((int)type);
                        break;
                    case 33437:
                        aperture = (float)await GetRealAsync((int)type);
                        break;
                    case 34303:
                        make.strncpy("Leaf".ToByteArray(), 4);
                        break;
                    case 34310:         /* Leaf metadata */
                        await ParseMos((int)ifp.Position);
                        make.strncpy("Leaf".ToByteArray(), 4);
                        break;
                    case 34665:
                        ifp.Seek(await Get4Async() + base_c, SeekOrigin.Begin);
                        await ParseExif(base_c);
                        break;
                    case 37386:
                        focal_len = (float)await GetRealAsync((int)type);
                        break;
                    case 37393:
                        shot_order = await GetIntAsync((int)type);
                        break;
                    case 51009: /* OpcodeList2 */
                        meta_offset = (uint)ifp.Position;
                        break;
                }
                ifp.Seek(save, SeekOrigin.Begin);
            }
            return 0;
        }

        private async Task ApplyTiff()
        {
            System.Diagnostics.Debug.WriteLine("Begin Apply Tiff");
            int i, max_samp = 0, raw = -1, thm = -1;
            JHead jh = new JHead();

            thumb_misc = 16;
            if (thumb_offset > 0)
            {
                ifp.Seek(thumb_offset, SeekOrigin.Begin);
                if (await LJpegStartAsync(jh, 1) > 0)
                {
                    thumb_misc = jh.bits;
                    thumb_width = jh.wide;
                    thumb_height = jh.high;
                }
            }

            for (i = (int)tiff_nifds - 1; i >= 0; i--)
            {
                if (tiff_ifd_c[i].shutter != 0)
                    shutter = tiff_ifd_c[i].shutter;
                tiff_ifd_c[i].shutter = shutter;
            }

            for (i = 0; i < tiff_nifds; i++)
            {
                if (max_samp < tiff_ifd_c[i].samples)
                    max_samp = tiff_ifd_c[i].samples;
                if (max_samp > 3)
                    max_samp = 3;
                if ((tiff_ifd_c[i].comp != 6 || tiff_ifd_c[i].samples != 3) &&
                    (tiff_ifd_c[i].width | tiff_ifd_c[i].height) < 0x10000 &&
                    tiff_ifd_c[i].width * tiff_ifd_c[i].height > raw_width * raw_height)
                {
                    raw_width = (ushort)tiff_ifd_c[i].width;
                    raw_height = (ushort)tiff_ifd_c[i].height;
                    tiff_bps = (uint)tiff_ifd_c[i].bps;

                    tiff_compress = (uint)tiff_ifd_c[i].comp;
                    data_offset = (uint)tiff_ifd_c[i].offset;
                    tiff_flip = (uint)tiff_ifd_c[i].flip;
                    tiff_samples = (uint)tiff_ifd_c[i].samples;
                    tile_width = (uint)tiff_ifd_c[i].tile_width;
                    tile_length = (uint)tiff_ifd_c[i].tile_length;
                    raw = i;
                }
            }

            for (i = (int)tiff_nifds - 1; i >= 0; i--)
            {
                if (tiff_ifd_c[i].flip != 0)
                    tiff_flip = (uint)tiff_ifd_c[i].flip;
            }

            for (i = 0; i < tiff_nifds; i++)
            {
                if (i != raw && tiff_ifd_c[i].samples == max_samp &&
                    tiff_ifd_c[i].width * tiff_ifd_c[i].height / ((tiff_ifd_c[i].bps * tiff_ifd_c[i].bps) + 1) >
                        thumb_width * thumb_height / ((thumb_misc * thumb_misc) + 1)
                    && tiff_ifd_c[i].comp != 34892)
                {
                    thumb_width = tiff_ifd_c[i].width;
                    thumb_height = tiff_ifd_c[i].height;
                    thumb_offset = (uint)tiff_ifd_c[i].offset;
                    thumb_length = (uint)tiff_ifd_c[i].bytes;
                    thumb_misc = tiff_ifd_c[i].bps;
                    thm = i;
                }
            }

            if (thm >= 0)
            {
                thumb_misc |= tiff_ifd_c[thm].samples << 5;
                switch (tiff_ifd_c[thm].comp)
                {
                    case 0:
                        write_thumb = WriteLayerThumb;
                        break;
                    case 1:
                        if (tiff_ifd_c[thm].bps <= 8)
                            write_thumb = WritePpmThumb;
                        else if (make.strcmp("Imacon"))
                            write_thumb = WritePpm16Thumb;
                        else
                            write_thumb = null;
                        break;
                    case 65000:
                        write_thumb = null;
                        break;
                }
            }
        }

        private async Task<int> LJpegStartAsync(JHead jh, int info_only)
        {
            System.Diagnostics.Debug.WriteLine("Begin LJpeg Start");
            ushort c = 0, tag, len;
            byte[] data = new byte[0x10000];

            jh.restart = Int32.MaxValue;
            ifp.ReadByte();
            if (ifp.ReadByte() != 0xd8)
                return 0;
            do
            {
                if (ifp.Read(data, 0, 4) == 0)
                    return 0;
                tag = (ushort)(data[0] << 8 | data[1]);
                len = (ushort)((data[2] << 8 | data[3]) - 2);
                if (tag <= 0xff00)
                    return 0;

                await ifp.ReadAsync(data, 0, len);

                switch (tag)
                {
                    case 0xffc3:
                        jh.sraw = ((data[7] >> 4) * (data[7] & 15) - 1) & 3;
                        jh.algo = tag & 0xff;
                        jh.bits = data[0];
                        jh.high = data[1] << 8 | data[2];
                        jh.wide = data[3] << 8 | data[4];
                        jh.clrs = data[5] + jh.sraw;
                        if (len == 9 && dng_version > 0)
                            ifp.ReadByte();
                        break;
                    case 0xffc1:
                    case 0xffc0:
                        jh.algo = tag & 0xff;
                        jh.bits = data[0];
                        jh.high = data[1] << 8 | data[2];
                        jh.wide = data[3] << 8 | data[4];
                        jh.clrs = data[5] + jh.sraw;
                        if (len == 9 && dng_version > 0)
                            ifp.ReadByte();
                        break;
                    case 0xffda:
                        jh.psv = data[1 + data[0] * 2];
                        jh.bits -= data[3 + data[0] * 2] & 15;
                        break;
                    case 0xffdb:
                        jh.quant[c] = (ushort)(data[c * 2 + 1] << 8 | data[c * 2 + 2]);
                        break;
                    case 0xffdd:
                        jh.restart = data[0] << 8 | data[1];
                        break;
                }
            }
            while (tag != 0xffda);

            // Info only support implemented for now
            return 1;
        }

        private async Task<TiffGetResult> TiffGet(UInt32 base_c)
        {
            uint tag = await Get2Async();
            uint type = await Get2Async();
            uint len = await Get4Async();
            uint save = (UInt32)(ifp.Position + 4);
            if (len * ("11124811248484"[type < 14 ? (int)type : 0] - '0') > 4)
                ifp.Seek(await Get4Async() + base_c, SeekOrigin.Begin);

            return new TiffGetResult(tag, type, len, save);
        }

        private async Task ParseExif(int base_c)
        {
            System.Diagnostics.Debug.WriteLine("Begin Parse Exif");
            UInt32 entries, tag = 0, type = 0, len = 0, save = 0;
            double expo;
            bool kodak;

            kodak = make.strcmp("EASTMAN", 7) && tiff_nifds < 3;

            entries = await Get2Async();
            while (entries-- > 0)
            {
                var result = await TiffGet((UInt32)base_c);
                tag = result.Tag;
                type = result.Type;
                len = result.Len;
                save = result.Save;

                switch (tag)
                {
                    case 33434:
                        tiff_ifd_c[(int)tiff_nifds - 1].shutter = shutter = (float)await GetRealAsync((int)type);
                        break;
                    case 33437:
                        aperture = (float)await GetRealAsync((int)type);
                        break;
                    case 34850:
                        exposure_program = await Get2Async();
                        break;
                    case 34855:
                        iso_speed = (float)await Get2Async();
                        break;
                    case 36867:
                    case 36868:
                        GetTimestamp(0);
                        break;
                    case 37377:
                        if ((expo = -(await GetRealAsync((int)type))) < 128)
                        {
                            tiff_ifd_c[(int)tiff_nifds - 1].shutter = shutter = (float)Math.Pow(2, expo);
                        }
                        break;
                    case 37378:
                        aperture = (float)Math.Pow(2, await GetRealAsync((int)type) / 2);
                        break;
                    case 37380:
                        exposure = (float)await GetRealAsync((int)type);
                        break;
                    case 37385:
                        flash = await Get2Async();
                        break;
                    case 37386:
                        focal_len = (float)await GetRealAsync((int)type);
                        break;
                    case 37500:
                        await ParseMakerNote(base_c, 0);
                        break;
                    case 40962:
                        if (kodak)
                        {
                            raw_width = (ushort)await Get4Async();
                        }
                        break;
                    case 40963:
                        if (kodak)
                        {
                            raw_height = (ushort)await Get4Async();
                        }
                        break;
                }

                ifp.Seek(save, SeekOrigin.Begin);
            }
        }

        private async Task ParseMakerNote(int base_c, int uptag)
        {
            System.Diagnostics.Debug.WriteLine("Begin Parse Makernote");
            uint offset = 0, entries, tag = 0, type = 0, len = 0, save = 0, c = 0;
            uint serial = 0, i, wbi = 0;
            uint[] wb = new uint[4] { 0, 0, 0, 0 };
            byte[] buf97 = new byte[324];
            short morder, sorder = order;
            byte[] buf = new byte[10];

            if (make.strcmp("Nokia")) return;

            ifp.Read(buf, 0, 10);

            if (buf.strcmp("Nikon"))
            {
                base_c = (int)ifp.Position;
                order = (short)await Get2Async();
                await Get2Async();
                offset = await Get4Async();
                ifp.Seek(offset - 8, SeekOrigin.Current);
            }
            else if (buf.strcmp("OLYMPUS") || buf.strcmp("PENTAX "))
            {
                base_c = (int)ifp.Position - 10;
                ifp.Seek(-2, SeekOrigin.Current);
                order = (short)await Get2Async();
                if (buf[0] == 'O')
                    await Get2Async();
            }
            else if (buf.strcmp("SONY", 4) ||
                buf.strcmp("Panasonic"))
            {
                order = 0x4949;
                ifp.Seek(2, SeekOrigin.Current);
            }
            else if (buf.strcmp("FUJIFILM", 8))
            {
                base_c = (int)ifp.Position - 10;
                order = 0x4949;
                ifp.Seek(2, SeekOrigin.Current);
            }
            else if (buf.strcmp("OLYMP") ||
                buf.strcmp("LEICA") ||
                buf.strcmp("Ricoh") ||
                buf.strcmp("EPSON"))
            {
                ifp.Seek(-2, SeekOrigin.Current);
            }
            else if (buf.strcmp("AOC") || buf.strcmp("QVC"))
            {
                ifp.Seek(-4, SeekOrigin.Current);
            }
            else
            {
                ifp.Seek(-10, SeekOrigin.Current);
                if (make.strcmp("SAMSUNG", 7))
                    base_c = (int)ifp.Position;
            }

            entries = await Get2Async();

            if (entries > 1000)
                return;

            morder = order;

            while (entries-- > 0)
            {
                order = morder;
                var result = await TiffGet((uint)base_c);
                tag = result.Tag;
                type = result.Type;
                len = result.Len;
                save = result.Save;

                tag |= (uint)uptag << 16;

                if (tag == 2 && make.strstr("NIKON") > 0 && iso_speed == 0)
                {
                    await Get2Async();
                    iso_speed = await Get2Async();
                }
                if (tag == 4 && len > 26 && len < 35)
                {
                    await Get4Async();
                    if ((i = await Get2Async()) != 0x7fff && iso_speed == 0)
                    {
                        iso_speed = (float)(50 * Math.Pow(2, i / 32.0 - 4));
                    }

                    await Get2Async();
                    if ((i = await Get2Async()) != 0x7fff && aperture == 0)
                    {
                        aperture = (float)Math.Pow(2, i / 64.0);
                    }

                    if ((i = await Get2Async()) != 0xffff && shutter == 0)
                    {
                        shutter = (float)Math.Pow(2, i / -32.0);
                    }

                    await Get2Async();
                    wbi = await Get2Async();

                    await Get2Async();
                    shot_order = await Get2Async();
                }
                if ((tag == 4 || tag == 0x114) && make.strcmp("KONICA", 6))
                {
                    ifp.Seek(tag == 4 ? 140 : 160, SeekOrigin.Current);
                    switch (await Get2Async())
                    {
                        case 72:
                            flip = 0;
                            break;
                        case 76:
                            flip = 6;
                            break;
                        case 82:
                            flip = 5;
                            break;
                    }
                }
                if (tag == 7 && type == 2 && len > 20)
                {
                    await ifp.ReadAsync(model2, 0, 64);
                }
                if (tag == 8 && type == 4)
                {
                    shot_order = await Get4Async();
                }
                if (tag == 0xd && type == 7 && await Get2Async() == 0xaaaa)
                {
                    for (c = i = 2; (ushort)c != 0xbbbb && i < len; i++)
                    {
                        c = c << 8 | Convert.ToByte(ifp.ReadByte());
                    }

                    while ((i += 4) < len - 5)
                    {
                        if (await Get4Async() == 257 && ((i = len) > 0) && (await Get4Async() >= 0) && (c = (uint)ifp.ReadByte()) < 3)
                        {
                            flip = (uint)("065"[(int)c] - '0');
                        }
                    }
                }
                if (tag == 0x10 && type == 4)
                {
                    unique_id = await Get4Async();
                }
                if (tag == 0x11 && is_raw > 0 && make.strcmp("NIKON", 5))
                {
                    ifp.Seek(await Get4Async() + base_c, SeekOrigin.Begin);
                    await ParseTiffIfdAsync(base_c);
                }
                if (tag == 0x15 && type == 2 && is_raw > 0)
                {
                    await ifp.ReadAsync(model, 0, 64);
                }
                if (make.strstr("PENTAX") >= 0)
                {
                    if (tag == 0x1b) tag = 0x1018;
                    if (tag == 0x1c) tag = 0x1017;
                }
                if (tag == 0x1d)
                {
                    var v = ifp.ReadByte();
                    while (v > -1)
                    {
                        c = (uint)v;
                        serial = serial * 10 + (Char.IsDigit(Convert.ToChar(c)) ? c - 0 : c % 10);
                        v = ifp.ReadByte();
                    }
                }
                if (tag == 0x81 && type == 4)
                {
                    data_offset = await Get4Async();
                    ifp.Seek(data_offset + 41, SeekOrigin.Begin);
                    raw_height = (ushort)(await Get2Async() * 2);
                    raw_width = await Get2Async();
                    filters = 0x61616161;
                }
                if ((tag == 0x81 && type == 7) ||
                    (tag == 0x100 && type == 7) ||
                    (tag == 0x280 && type == 1))
                {
                    thumb_offset = (uint)ifp.Position;
                    thumb_length = len;
                }
                if (tag == 0x88 && type == 4 && ((thumb_offset = (uint)await Get4Async()) > 0))
                {
                    thumb_offset += (uint)base_c;
                }
                if (tag == 0x89 && type == 4)
                {
                    thumb_length = await Get4Async();
                }
                if (tag == 0x8c || tag == 0x96)
                {
                    meta_offset = (uint)ifp.Position;
                }
                if (tag == 0x200 && len == 3)
                {
                    await Get4Async();
                    shot_order = await Get4Async();
                }
                if (tag == 0x220 && type == 7)
                    meta_offset = (uint)ifp.Position;
                if (tag == 0xe01) /* Nikon Capture Note */
                {
                    order = 0x4949;
                    ifp.Seek(22, SeekOrigin.Current);
                    for (offset = 22; offset + 22 < len; offset += 22 + i)
                    {
                        tag = await Get4Async();
                        ifp.Seek(14, SeekOrigin.Current);
                        i = await Get4Async() - 4;
                        if (tag == 0x76a43207)
                            flip = await Get2Async();
                        else
                            ifp.Seek(i, SeekOrigin.Current);
                    }
                }
                if ((tag | 0x70) == 0x2070 && (type == 4 || type == 13))
                {
                    ifp.Seek(await Get4Async() + base_c, SeekOrigin.Begin);
                }
                if (tag == 0x2020)
                {
                    await ParseThumbNote(base_c, 257, 258);
                }
                if (tag == 0x2040)
                {
                    await ParseMakerNote(base_c, 0x2040);
                }
                if (tag == 0xb028)
                {
                    ifp.Seek(await Get4Async() + base_c, SeekOrigin.Begin);
                    await ParseThumbNote(base_c, 136, 137);
                }
                if (tag == 0xb001)
                {
                    unique_id = await Get2Async();
                }

                ifp.Seek(save, SeekOrigin.Begin);
            }
            order = sorder;
        }

        private async Task ParseThumbNote(int base_c, uint toff, uint tlen)
        {
            System.Diagnostics.Debug.WriteLine("Begin Parse Thumbnote");
            uint entries, tag = 0, type = 0, len = 0, save = 0;

            entries = await Get2Async();

            if (entries > 1000)
                return;

            while (entries-- > 0)
            {
                var result = await TiffGet((uint)base_c);
                tag = result.Tag;
                type = result.Type;
                len = result.Len;
                save = result.Save;

                if (tag == toff)
                    thumb_offset = (uint)(await Get4Async() + base_c);
                if (tag == tlen)
                    thumb_length = await Get4Async();

                ifp.Seek(save, SeekOrigin.Begin);
            }
        }

        #endregion

        #region Thumbnail Writing

        private async Task<MemoryStream> WriteJpegThumb()
        {
            System.Diagnostics.Debug.WriteLine("Begin Write Jpeg Thumb");
            byte[] thumb = new byte[thumb_length];
            ushort[] exif = new ushort[5];
            TiffHdr th = new TiffHdr();

            ifp.Read(thumb, 0, (int)thumb_length);

            outStream = new MemoryStream();

            outStream.WriteByte(0xff);
            outStream.WriteByte(0xd8);

            if (!thumb.SubArray(6).strcmp("Exif"))
            {
                var rawSize = Marshal.SizeOf(th);

                exif[0] = (ushort)(NumberHelper.NetworkToHostOrder(0xffe1) >> 16);
                exif[1] = (ushort)NumberHelper.NetworkToHostOrder((short)(rawSize + 8));
                exif[2] = (ushort)(NumberHelper.NetworkToHostOrder(0x4578) >> 16);
                exif[3] = (ushort)(NumberHelper.NetworkToHostOrder(0x6966) >> 16);
                exif[4] = 0;

                await outStream.WriteAsync(exif.ToByteArray(), 0, 10);

                TiffHead(th, 0);

                IntPtr buffer = Marshal.AllocHGlobal(rawSize);
                Marshal.StructureToPtr(th, buffer, true);

                byte[] rawDatas = new byte[rawSize];
                Marshal.Copy(buffer, rawDatas, 0, rawSize);
                Marshal.FreeHGlobal(buffer);

                await outStream.WriteAsync(rawDatas, 0, rawSize);
            }

            await outStream.WriteAsync(thumb, 2, (int)thumb_length - 2);

			outStream.Position = 0;

            return outStream;
        }

        private void TiffHead(TiffHdr th, int full)
        {
            System.Diagnostics.Debug.WriteLine("Begin Tiff Head");
            int i;

            th.order = (ushort)(NumberHelper.HostToNetworkOrder(0x4d4d4949) >> 16);
            th.magic = 42;
            th.ifd = 10;

            th.rat[0] = th.rat[2] = 300;
            th.rat[1] = th.rat[3] = 1;
            for (i = 0; i < 8; i++)
                th.rat[4 + i] = 1000000;
            th.rat[4] = (int)(th.rat[4] * shutter);
            th.rat[6] = (int)(th.rat[6] * aperture);
            th.rat[8] = (int)(th.rat[8] * focal_len);
            th.rat[10] = (int)(th.rat[10] * exposure);
            th.desc.strncpy(desc, 512);
            th.make.strncpy(make, 64);
            th.model.strncpy(model, 64);
            th.date = timestamp.ToString("yyyy:MM:dd HH:mm:ss\0").ToByteArray();
            th.artist.strncpy(artist, 64);

            TiffSet(th.tag, ref th.ntag, 270, 2, 512, 664, th.desc); // Description
            TiffSet(th.tag, ref th.ntag, 271, 2, 64, 1176, th.make); // Make
            TiffSet(th.tag, ref th.ntag, 272, 2, 64, 1240, th.model); // Model

            TiffSet(th.tag, ref th.ntag, 274, 3, 1, "12435867"[(int)(flip > 7 ? 0 : flip)] - '0', null);

            TiffSet(th.tag, ref th.ntag, 282, 5, 1, 512, null); // DPI
            TiffSet(th.tag, ref th.ntag, 283, 5, 1, 520, null); // DPI
            TiffSet(th.tag, ref th.ntag, 284, 3, 1, 1, null);
            TiffSet(th.tag, ref th.ntag, 296, 3, 1, 2, null);
            TiffSet(th.tag, ref th.ntag, 305, 2, 32, 1304, th.soft);
            TiffSet(th.tag, ref th.ntag, 306, 2, 20, 1336, th.date);
            TiffSet(th.tag, ref th.ntag, 315, 2, 64, 1356, th.artist);
            TiffSet(th.tag, ref th.ntag, 34665, 4, 1, 294, null);

            TiffSet(th.exif, ref th.nexif, 33434, 5, 1, 528, null); // Shutter
            TiffSet(th.exif, ref th.nexif, 33437, 5, 1, 536, null); // Aperture
            TiffSet(th.exif, ref th.nexif, 34850, 3, 1, (int)exposure_program, null); // Exposure Program
            TiffSet(th.exif, ref th.nexif, 34855, 3, 1, (int)iso_speed, null); // ISO
            TiffSet(th.exif, ref th.nexif, 37380, 10, 1, 552, null); // Exposure Compensation
            TiffSet(th.exif, ref th.nexif, 37385, 3, 1, (int)flash, null); // Flash
            TiffSet(th.exif, ref th.nexif, 37386, 5, 1, 544, null); // Focal_Len
        }

        private void TiffSet(TiffTag[] ttArr, ref ushort ntag, ushort tag, ushort type, int count, int val, byte[] data)
        {
            TiffTag tt = new TiffTag();
            int i;
            byte[] val_c = new byte[4];
            short[] val_s = new short[2];

            ntag++;

            tt.val = val;

            if (type == 1 && count <= 4)
            {
                for (i = 0; i < 4; i++)
                    val_c[i] = (byte)(val >> (i << 3));
                tt.val = BitConverter.ToInt32(val_c, 0);
            }
            else if (type == 2)
            {
                count = data.strnlen(count - 1) + 1;
                if (count <= 4)
                {
                    for (i = 0; i < 4; i++)
                    {
                        val_c[i] = data[i];
                    }
                    tt.val = BitConverter.ToInt32(val_c, 0);
                }
            }
            else if (type == 3 && count <= 2)
            {
                for (i = 0; i < 2; i++)
                    val_s[i] = (short)(val >> (i << 4));
                tt.val = BitConverter.ToInt32(val_s.ToByteArray(), 0);
            }

            tt.count = count;
            tt.type = type;
            tt.tag = tag;

            ttArr[ntag - 1] = tt;
        }

        private async Task<MemoryStream> WriteLayerThumb()
        {
            // Not implemented at this time
            return null;
        }

        private async Task<MemoryStream> WritePpmThumb()
        {
            // Not implemented at this time
            return null;
        }

        private async Task<MemoryStream> WritePpm16Thumb()
        {
            // Not implemented at this time
            return null;
        }

        private async Task<MemoryStream> WriteFoveonThumb()
        {
            // Not implemented at this time
            return null;
        }

        #endregion

        #region Bitwise Tools

        private async Task<ushort> Get2Async()
        {
            byte[] str = new byte[2] { 0xff, 0xff };
            await ifp.ReadAsync(str, 0, 2);
            if (order == 0x4949)
                return (ushort)(str[0] | str[1] << 8);
            else
                return (ushort)(str[0] << 8 | str[1]);
        }

        private async Task<UInt32> Get4Async()
        {
            byte[] str = new byte[4] { 0xff, 0xff, 0xff, 0xff };
            await ifp.ReadAsync(str, 0, 4);
            if (order == 0x4949)
                return (UInt32)(str[0] | str[1] << 8 | str[2] << 16 | str[3] << 24);
            else
                return (UInt32)(str[0] << 24 | str[1] << 16 | str[2] << 8 | str[3]);
        }

        private async Task<UInt32> GetIntAsync(int type)
        {
            return type == 3 ? await Get2Async() : await Get4Async();
        }

        private float IntToFloat(int i)
        {
            return (float)i;
        }

        private async Task<double> GetRealAsync(int type)
        {
            double d;
            byte[] c = new byte[8];

            switch (type)
            {
                case 3: return await Get2Async();
                case 4: return await Get4Async();
                case 5:
                    d = await Get4Async();
                    return d / await Get4Async();
                case 8: return (short)await Get2Async();
                case 9: return (int)await Get4Async();
                case 10:
                    d = (int)await Get4Async();
                    return d / (int)await Get4Async();
                case 11: return IntToFloat((int)await Get4Async());
                case 12:
                    int i, rev;
                    rev = 7 * ((order == 0x4949) == (NumberHelper.NetworkToHostOrder(0x1234) == 0x1234) ? 1 : 0);
                    for (i = 0; i < 8; i++)
                    {
                        c[i ^ rev] = Convert.ToByte(ifp.ReadByte());
                    }
                    return BitConverter.ToDouble(c, 0);
                default:
                    return ifp.ReadByte();
            }
        }

        /* Since theh TIFF DateTime string has no timezone information,
         * assume that the camera's clock was set to Universal Time
         */
        private void GetTimestamp(int reversed)
        {
            byte[] bytes = new byte[20];
            int[] timeParts = new int[6];
            int i;
            DateTimeOffset ts;

            bytes[19] = 0;
            if (reversed != 0)
            {
                for (i = 19; i >= 0; i--)
                {
                    bytes[i] = (byte)ifp.ReadByte();
                }
            }
            else
            {
                ifp.Read(bytes, 0, 19);
            }

            var str = bytes.ToNullTerminatedString();
            var parts = str.Trim().Split(new char[] { ' ', ':' });

            if (parts.Length >= 6)
            {
                try
                {
                    for (i = 0; i < 6; i++)
                    {
                        timeParts[i] = int.Parse(parts[i]);
                    }

                    ts = new DateTimeOffset(timeParts[0], timeParts[1], timeParts[2],
                        timeParts[3], timeParts[4], timeParts[5], TimeSpan.Zero);

                    timestamp = ts;
                }
                catch
                {
                    // Timestamp unable to be parsed. In this this case we'll just do
                    // nothing and not have timestamp on this file.
                }
            }

        }

        #endregion

    }
}
