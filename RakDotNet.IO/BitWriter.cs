﻿using System;
using System.IO;
using System.Runtime.InteropServices;

namespace RakDotNet.IO
{
    public class BitWriter : IDisposable
    {
        private readonly Stream _stream;
        private readonly bool _leaveOpen;
        private readonly bool _orderLocked;
        private readonly object _lock;

        private Endianness _endianness;
        private bool _disposed;
        private long _pos;

        public virtual Stream BaseStream => _stream;
        public virtual Endianness Endianness
        {
            get => _endianness;

            set
            {
                if (_orderLocked)
                    throw new InvalidOperationException("Endianness is fixed");

                if (value != _endianness)
                {
                    // wait for write operations to complete so we don't mess them up
                    lock (_lock)
                    {
                        _endianness = value;
                    }
                }
            }
        }

        public BitWriter(Stream stream)
            : this(stream, Endianness.LittleEndian, true, true)
        {
        }

        public BitWriter(Stream stream, Endianness endianness)
            : this(stream, endianness, true, false)
        {
        }

        public BitWriter(Stream stream, Endianness endianness, bool orderLocked)
            : this(stream, endianness, orderLocked, false)
        {
        }

        public BitWriter(Stream stream, Endianness endianness, bool orderLocked, bool leaveOpen)
        {
            if (!stream.CanWrite)
                throw new ArgumentException("Stream is not writeable", nameof(stream));

            if (!stream.CanRead)
                throw new ArgumentException("Stream is not readable", nameof(stream));

            _stream = stream;
            _leaveOpen = leaveOpen;
            _orderLocked = orderLocked;
            _lock = new object();

            _endianness = endianness;
            _disposed = false;
            _pos = 0;
        }

        protected virtual void Dispose(bool disposing)
        {
            if (disposing)
            {
                if (_leaveOpen)
                    _stream.Flush();
                else
                    _stream.Close();
            }
        }

        public void Dispose() => Dispose(true);

        public virtual void Close() => Dispose(true);

        public virtual void WriteBit(bool bit)
        {
            lock (_lock)
            {
                // offset in bits, in case we're not starting on the 8th (i = 7) bit
                var bitOffset = (byte)(_pos & 7);

                // read the last byte from the stream 
                var val = _stream.ReadByte();

                // ReadByte returns -1 if we reached the end of the stream, we need unsigned data so set it to 0
                if (val < 0)
                    val = 0;

                // if we're starting on a new byte, write 0x80 (0b10000000), else shift the bit we want to write to the right by bitOffset
                var bitVal = (byte)((bitOffset == 0) ? 0x80 : (0x80 >> bitOffset));

                // we use bitwise OR if we want to set bits to 1, bitwise XOR if we want to set it to 0
                if (bit)
                    val |= bitVal;
                else
                    val ^= bitVal;

                // go back 1 byte to overwrite the previously written byte
                //_stream.Position--;

                // write the modified byte to the stream
                _stream.WriteByte((byte)val);

                // advance the bit position
                _pos++;

                // if we aren't ending on a new byte, go back 1 byte on the stream
                if ((_pos & 7) != 0)
                    _stream.Position--;
            }
        }

        public virtual int Write(ReadOnlySpan<byte> buf, int bits)
        {
            // offset in bits, in case we're not starting on the 8th (i = 7) bit
            var bitOffset = (byte)(_pos & 7);

            // inverted bit offset (eg. 3 becomes 5)
            var invertedOffset = (byte)(-bitOffset & 7);

            // get num of bytes we have to read from the Stream, we add bitOffset so we have enough data to add in case bitOffset != 0
            var byteCount = (int)Math.Ceiling((bits + bitOffset) / 8d);

            // get size of output buffer
            var bufSize = (int)Math.Ceiling(bits / 8d);

            // lock the read so we don't mess up other calls
            lock (_lock)
            {
                // check if we don't have to do complex bit level operations
                if (bitOffset == 0 && (bits & 7) == 0)
                {
                    _stream.Write(buf);

                    _pos += bits;

                    return bufSize;
                }

                // allocate a buffer on the stack to write 
                Span<byte> bytes = stackalloc byte[byteCount];

                // we might already have data in the stream
                _stream.Position -= _stream.Read(bytes);

                if (bitOffset != 0)
                    _stream.Position++;

                for (var i = 0; bits > 0; i++)
                {
                    // add bits starting from bitOffset from the input buffer to the write buffer
                    bytes[i] |= (byte)(buf[i] >> bitOffset);

                    // set the leaking bits on the next byte
                    if (invertedOffset < 8 && bits > invertedOffset)
                        bytes[i + 1] = (byte)(buf[i] << invertedOffset);

                    // add max 8 remaining bits to the position
                    _pos += bits < 8 ? bits & 7 : 8;

                    // we wrote a byte, remove 8 bits from the bit count
                    bits -= 8;

                    // if we're at the last byte, cut off the unused bits
                    if (bits < 8)
                        bytes[i] <<= (-bits & 7);
                }

                // swap endianness in case we're not using same endianness as host
                if ((_endianness != Endianness.LittleEndian && BitConverter.IsLittleEndian) ||
                    (_endianness != Endianness.BigEndian && !BitConverter.IsLittleEndian))
                    bytes.Reverse();

                // write the buffer
                _stream.Write(bytes);

                // add written byte count to the position of the stream
                _stream.Position += bufSize;
            }

            return bufSize;
        }

        public virtual int Write(Span<byte> buf, int bits)
            => Write((ReadOnlySpan<byte>)buf, bits);

        public virtual int Write(byte[] buf, int index, int length, int bits)
        {
            if (bits > (length * 8))
                throw new ArgumentOutOfRangeException(nameof(bits), "Bit count exceeds buffer length");

            if (index > length)
                throw new ArgumentOutOfRangeException(nameof(index), "Index exceeds buffer length");

            return Write(new ReadOnlySpan<byte>(buf, index, length), bits);
        }

        public virtual int Write<T>(T val, int bits) where T : struct
        {
            var size = Marshal.SizeOf<T>();
            var buf = new byte[size];
            var ptr = Marshal.AllocHGlobal(size);

            Marshal.StructureToPtr<T>(val, ptr, false);
            Marshal.Copy(ptr, buf, 0, size);

            return Write(new ReadOnlySpan<byte>(buf), bits);
        }

        public virtual int Write<T>(T val) where T : struct
            => Write<T>(val, Marshal.SizeOf<T>() * 8);
    }
}