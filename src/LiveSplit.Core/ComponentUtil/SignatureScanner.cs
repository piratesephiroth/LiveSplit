﻿using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Linq;

// Note: Please be careful when modifying this because it could break existing components!

namespace LiveSplit.ComponentUtil;

public class SignatureScanner
{
    private byte[] _memory;
    private Process _process;
    private nint _address;
    private int _size;

    public nint Address
    {
        get => _address;
        set
        {
            _memory = null;
            _address = value;
        }
    }

    public int Size
    {
        get => _size;
        set
        {
            _memory = null;
            _size = value;
        }
    }

    public Process Process
    {
        get => _process;
        set
        {
            _memory = null;
            _process = value;
        }
    }

    public byte[] Memory
    {
        get => _memory;
        set
        {
            _memory = value;
            _size = value.Length;
        }
    }

    public SignatureScanner(Process proc, nint addr, int size)
    {
        if (proc == null)
        {
            throw new ArgumentNullException(nameof(proc));
        }

        if (addr == 0)
        {
            throw new ArgumentException("addr cannot be 0.", nameof(addr));
        }

        if (size <= 0)
        {
            throw new ArgumentException("size cannot be less than zero.", nameof(size));
        }

        _process = proc;
        _address = addr;
        _size = size;
        _memory = new byte[1];
    }

    public SignatureScanner(byte[] mem)
    {
        if (mem == null)
        {
            throw new ArgumentNullException(nameof(mem));
        }

        _memory = mem;
        _size = mem.Length;
    }

    // backwards compat method signature
    public nint Scan(SigScanTarget target)
    {
        return Scan(target, 1);
    }

    public nint Scan(SigScanTarget target, int align)
    {
        if ((long)_address % align != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(align), "start address must be aligned");
        }

        return ScanAll(target, align).FirstOrDefault();
    }

    public IEnumerable<nint> ScanAll(SigScanTarget target, int align = 1)
    {
        if ((long)_address % align != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(align), "start address must be aligned");
        }

        return ScanInternal(target, align);
    }

    private IEnumerable<nint> ScanInternal(SigScanTarget target, int align)
    {
        if (_memory == null || _memory.Length != _size)
        {

            if (!_process.ReadBytes(_address, _size, out byte[] bytes))
            {
                _memory = null;
                yield break;
            }

            _memory = bytes;
        }

        foreach (SigScanTarget.Signature sig in target.Signatures)
        {
            // have to implement IEnumerator manually because you can't yield in an unsafe block...
            foreach (int off in new ScanEnumerator(_memory, align, sig))
            {
                nint ptr = _address + off + sig.Offset;
                if (target.OnFound != null)
                {
                    ptr = target.OnFound(_process, this, ptr);
                }

                yield return ptr;
            }
        }
    }

    private class ScanEnumerator : IEnumerator<int>, IEnumerable<int>
    {
        // IEnumerator
        public int Current { get; private set; }
        object IEnumerator.Current => Current;

        private readonly byte[] _memory;
        private readonly int _align;
        private readonly SigScanTarget.Signature _sig;

        private readonly int _sigLen;
        private readonly int _end;

        private int _nextIndex;

        public ScanEnumerator(byte[] mem, int align, SigScanTarget.Signature sig)
        {
            if (mem.Length < sig.Pattern.Length)
            {
                throw new ArgumentOutOfRangeException(nameof(mem), "memory buffer length must be >= pattern length");
            }

            _memory = mem;
            _align = align;
            _sig = sig;

            _sigLen = _sig.Pattern.Length;
            _end = _memory.Length - _sigLen;
        }

        // IEnumerator
        public bool MoveNext()
        {
            return _sig.Mask != null ? NextPattern() : NextBytes();
        }
        public void Reset()
        {
            _nextIndex = 0;
        }
        public void Dispose()
        {
        }

        // IEnumerable
        public IEnumerator<int> GetEnumerator()
        {
            return this;
        }
        IEnumerator IEnumerable.GetEnumerator()
        {
            return this;
        }

        private unsafe bool NextPattern()
        {
            fixed (bool* mask = _sig.Mask)
            fixed (byte* mem = _memory, sig = _sig.Pattern)
            {
                // perf: locals are MUCH faster than properties and fields, especially on writes
                int end = _end;
                int sigLen = _sigLen;
                int align = _align;
                int index = _nextIndex; // biggest speed increase

                for (; index < end; index += align) // index++ would be ~7% faster
                {
                    for (int sigIndex = 0; sigIndex < sigLen; sigIndex++)
                    {
                        if (mask[sigIndex])
                        {
                            continue;
                        }

                        if (sig[sigIndex] != mem[index + sigIndex])
                        {
                            goto next;
                        }
                    }

                    // fully matched
                    Current = index;
                    _nextIndex = index + align;
                    return true;

                next:
                    ;
                }

                return false;
            }
        }

        private unsafe bool NextBytes()
        {
            // just a straight memory compare
            fixed (byte* mem = _memory, sig = _sig.Pattern)
            {
                int end = _end;
                int index = _nextIndex;
                int align = _align;
                int sigLen = _sigLen;

                for (; index < end; index += align)
                {
                    for (int sigIndex = 0; sigIndex < sigLen; sigIndex++)
                    {
                        if (sig[sigIndex] != mem[index + sigIndex])
                        {
                            goto next;
                        }
                    }

                    // fully matched
                    Current = index;
                    _nextIndex = index + align;
                    return true;

                next:
                    ;
                }

                return false;
            }
        }
    }
}

public class SigScanTarget
{
    public struct Signature
    {
        public byte[] Pattern;
        public bool[] Mask;
        public int Offset;
    }

    public delegate nint OnFoundCallback(Process proc, SignatureScanner scanner, nint ptr);
    public OnFoundCallback OnFound { get; set; }

    private readonly List<Signature> _sigs;
    public ReadOnlyCollection<Signature> Signatures => _sigs.AsReadOnly();

    public SigScanTarget()
    {
        _sigs = [];
    }

    public SigScanTarget(int offset, params string[] signature)
        : this()
    {
        AddSignature(offset, signature);
    }

    public SigScanTarget(int offset, params byte[] signature)
        : this()
    {
        AddSignature(offset, signature);
    }

    public SigScanTarget(params string[] signature) : this(0, signature) { }
    // make sure to cast the first arg to byte if using params, so you don't accidentally use offset ctor
    public SigScanTarget(params byte[] binary) : this(0, binary) { }

    public void AddSignature(int offset, params string[] signature)
    {
        string sigStr = string.Join(string.Empty, signature).Replace(" ", string.Empty);
        if (sigStr.Length % 2 != 0)
        {
            throw new ArgumentException(nameof(signature));
        }

        var sigBytes = new List<byte>();
        var sigMask = new List<bool>();
        bool hasMask = false;

        for (int i = 0; i < sigStr.Length; i += 2)
        {
            if (byte.TryParse(sigStr.Substring(i, 2), NumberStyles.HexNumber, null, out byte b))
            {
                sigBytes.Add(b);
                sigMask.Add(false);
            }
            else
            {
                sigBytes.Add(0);
                sigMask.Add(true);
                hasMask = true;
            }
        }

        _sigs.Add(new Signature
        {
            Pattern = [.. sigBytes],
            Mask = hasMask ? [.. sigMask] : null,
            Offset = offset,
        });
    }

    public void AddSignature(int offset, params byte[] binary)
    {
        _sigs.Add(new Signature
        {
            Pattern = binary,
            Mask = null,
            Offset = offset,
        });
    }

    public void AddSignature(params string[] signature)
    {
        AddSignature(0, signature);
    }

    public void AddSignature(params byte[] binary)
    {
        AddSignature(0, binary);
    }
}
