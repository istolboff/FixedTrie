using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Runtime.CompilerServices;
using System.Text;

namespace Tests
{
    public class MemoryAllocator
    {
        public MemoryAllocator()
        {
            _buffer = new byte[32 * 1024];

            var wholeBufferRegion = new BufferRegion(_buffer, regionLength: _buffer.Length, regionStart: 0);
            var (firtsFreeBlockReferenceHolder, initialFreeBlock) = wholeBufferRegion.Split(sizeof(short));

            var theOnlyFreeBlock = AllocationBlock.SetupBlock(initialFreeBlock, true);
            new FreeBlock(theOnlyFreeBlock).NextFreeBlockReference.Reset();

            _referenceToFirstFreeBlock = new MemoryReference(firtsFreeBlockReferenceHolder);
            _referenceToFirstFreeBlock.Set(theOnlyFreeBlock);
        }

        public FreeBlock? FirstFreeBlock
        {
            get => FreeBlock.FromReference(_referenceToFirstFreeBlock);
        }

        public BufferRegion? Allocate(short size)
        {
            if (size <= 0)
            {
                return null;
            }

            size = Math.Max(size, Cast.ToShort(2 * sizeof(short)));

            var bestBlockSoFar = ((MemoryReference, FreeBlock)?)null;
            for (var currentFreeBlockReference = _referenceToFirstFreeBlock; !currentFreeBlockReference.IsNull;)
            {
                var currentFreeBlock = FreeBlock.FromReference(currentFreeBlockReference).Value;
                if (currentFreeBlock.Length >= size * 2)
                {
                    return CoreAllocate(size, currentFreeBlockReference, currentFreeBlock);
                }

                if (currentFreeBlock.Length >= size && (bestBlockSoFar == null || bestBlockSoFar.Value.Item2.Length < currentFreeBlock.Length))
                {
                    bestBlockSoFar = (currentFreeBlockReference, currentFreeBlock);
                }

                currentFreeBlockReference = currentFreeBlock.NextFreeBlockReference;
            }

            return bestBlockSoFar == null
                    ? (BufferRegion?)null
                    : CoreAllocate(size, bestBlockSoFar.Value.Item1, bestBlockSoFar.Value.Item2);
        }

        public void Deallocate(BufferRegion memoryRegion)
        {
            var reconstructedBlock = ReconstructAllocationBlock(memoryRegion);
            var freedBlock = reconstructedBlock.MarkAsFree();

            var previousBlock = reconstructedBlock.PreviousBlock;
            var nextBlock = reconstructedBlock.NextBlock;

            if (previousBlock == null)
            {
                if (nextBlock == null || !nextBlock.Value.IsFree)
                {
                    freedBlock.NextFreeBlockReference.Set(_referenceToFirstFreeBlock);
                }
                else
                {
                    freedBlock.JoinWith(new FreeBlock(nextBlock.Value));
                }

                _referenceToFirstFreeBlock.Set(reconstructedBlock);
            }
            else if (!previousBlock.Value.IsFree)
            {
                var previousFreeReference = GetPreviousFreeReference(previousBlock.Value);
                if (nextBlock != null)
                {
                    if (!nextBlock.Value.IsFree)
                    {
                        freedBlock.NextFreeBlockReference.Set(previousFreeReference);
                    }
                    else
                    {
                        freedBlock.JoinWith(new FreeBlock(nextBlock.Value));
                    }
                }

                previousFreeReference.Set(reconstructedBlock);
            }
            else
            {
                var previousFreeBlock = new FreeBlock(previousBlock.Value);
                freedBlock.NextFreeBlockReference.Set(previousFreeBlock.NextFreeBlockReference);
                var joinedBlock = previousFreeBlock.JoinWith(freedBlock);
                if (nextBlock != null && nextBlock.Value.IsFree)
                {
                    joinedBlock.JoinWith(new FreeBlock(nextBlock.Value));
                }
            }
        }

        public BufferRegion Reallocate(BufferRegion memoryRegion, short newSize)
        {
            throw new NotImplementedException();
        }

        public override string ToString()
        {
            var stringBuilder = new StringBuilder();
            for (AllocationBlock? currentBlock = FirstBlock; currentBlock != null; currentBlock = currentBlock.Value.NextBlock)
            {
                stringBuilder.Append(currentBlock.Value);
            }
            stringBuilder.AppendLine();
            for (FreeBlock? currentFreeBlock = FirstFreeBlock; currentFreeBlock != null; currentFreeBlock = currentFreeBlock.Value.NextFreeBlock)
            {
                stringBuilder.Append(currentFreeBlock.Value);
            }

            return stringBuilder.ToString();
        }

        private AllocationBlock FirstBlock
        {
            get
            {
                return ReconstructAllocationBlock(new BufferRegion(_buffer, 2 * sizeof(short), 2));
            }
        }

        private BufferRegion CoreAllocate(short size, MemoryReference freeBlockReference, FreeBlock freeBlock)
        {
            Assert.IsTrue(freeBlock.Length >= size);
            var useTheWholeFreeBlockForAllocation = freeBlock.Length < size + 4 * sizeof(short);
            if (useTheWholeFreeBlockForAllocation)
            {
                freeBlockReference.Set(freeBlock.NextFreeBlockReference);
                return freeBlock.Use();
            }
            else
            {
                var (firstFreeBlock, secondFreeBlock) = freeBlock.Split(size);
                freeBlockReference.Set(secondFreeBlock.AllocationBlock);
                return firstFreeBlock.Use();
            }
        }

        private AllocationBlock ReconstructAllocationBlock(BufferRegion memoryRegion)
        {
            var blockSizeRegion = memoryRegion.GetLeftAdjacentRegion(sizeof(short));
            return new AllocationBlock(blockSizeRegion.WithLength(AllocationBlock.ReadBlockSize(blockSizeRegion)));
        }

        private MemoryReference GetPreviousFreeReference(AllocationBlock? block)
        {
            while (block != null && !block.Value.IsFree)
            {
                block = block.Value.PreviousBlock;
            }

            return block == null ? _referenceToFirstFreeBlock : new FreeBlock(block.Value).NextFreeBlockReference;
        }

        private readonly MemoryReference _referenceToFirstFreeBlock;

        private readonly byte[] _buffer;
    }


    public readonly struct BufferRegion : IEquatable<BufferRegion>
    {
        public BufferRegion(byte[] memory, short regionStart, int regionLength)
        {
            Verify.That(regionStart >= 0, "regionStart >= 0");
            Verify.That(regionLength > 0, "regionLength > 0");
            Verify.That(regionStart + regionLength <= memory.Length, "regionStart + regionLength <= memory.Length");

            Buffer = memory;
            Start = regionStart;
            Length = regionLength;
        }

        public bool IsEmpty => Length == 0;

        public readonly byte[] Buffer;

        public readonly short Start;

        public readonly int Length;

        public int End
        {
            get => Start + Length;
        }

        public void WriteAt(short offset, byte value)
        {
            Verify.That(offset < Length);
            Buffer[Start + offset] = value;
        }

        public void SetContent(byte value)
        {
            for (var offset = 0; offset != Length; ++offset)
            {
                CoreWriteAt(value: value, offset: Cast.ToShort(offset));
            }
        }

        public BufferRegion Slice(short start, int length)
        {
            Verify.That(start < Length); // "Slicing would result in an empty MemoryRegion"
            Verify.That(start + length <= Length); // "Slicing would result in a region spawned past the end of MemoryRegion"
            return new BufferRegion(Buffer, regionStart: Cast.ToShort(Start + start), regionLength: length);
        }

        public BufferRegion JoinWith(BufferRegion nextRegion)
        {
            Verify.That(Start + Length == nextRegion.Start);
            return new BufferRegion(Buffer, regionStart: Start, regionLength: Length + nextRegion.Length);
        }

        public (BufferRegion, BufferRegion) Split(short firstRegionSize)
        {
            Verify.That(firstRegionSize < Length);
            return (
                    Slice(start: 0, length: firstRegionSize),
                    Slice(start: firstRegionSize, length: Length - firstRegionSize)
                   );
        }

        public BufferRegion GetLeftAdjacentRegion(int length)
        {
            Verify.That(Start >= length);
            return new BufferRegion(Buffer, regionStart: Cast.ToShort(Start - length), regionLength: length);
        }

        public BufferRegion GetRightAdjacentRegion(int length)
        {
            Verify.That(Start + Length + length <= Buffer.Length);
            return new BufferRegion(Buffer, regionStart: Cast.ToShort(Start + Length), regionLength: length);
        }

        public BufferRegion WithLength(int length)
        {
            Verify.That(length > 0);
            Verify.That(Start + length <= Buffer.Length);
            return new BufferRegion(Buffer, regionStart: Start, regionLength: length);
        }

        public bool Equals(BufferRegion other) =>
            ReferenceEquals(Buffer, other.Buffer) &&
            Start == other.Start &&
            Length == other.Length;

        public override bool Equals(object other) =>
            other != null && other is BufferRegion && Equals((BufferRegion)other);

        public override int GetHashCode() =>
            Start.GetHashCode();

        public override string ToString()
        {
            return $"[{Start}, {Start + Length}]";
        }

        public static bool operator ==(BufferRegion left, BufferRegion right) =>
            left.Equals(right);

        public static bool operator !=(BufferRegion left, BufferRegion right) =>
            !(left == right);

        public void WriteAt(short offset, short value)
        {
            Verify.That(offset <= Length - sizeof(short)); // "Attempt to write short past the end of the memory region."
            CoreWriteAt(value: value, offset: offset);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal short CoreReadShortAt(short offset) =>
            (short)(Buffer[Start + offset] | (Buffer[Start + offset + 1] << 8));

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal void CoreWriteAt(short offset, short value)
        {
            Buffer[Start + offset] = (byte)(value & 0xff);
            Buffer[Start + offset + 1] = (byte)(value >> 8);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal void CoreWriteAt(short offset, byte value)
        {
            Buffer[Start + offset] = value;
        }
    }

    public readonly struct AllocationBlock
    {
        public AllocationBlock(BufferRegion bufferRegion)
        {
            Assert.IsTrue(bufferRegion.Length >= 4 * sizeof(short));
            _bufferRegion = bufferRegion;
        }

        public bool IsFree
        {
            get
            {
                Assert.IsTrue(LengthAtHead == LengthAtTail);
                return LengthAtHead > 0;
            }
        }

        public short Start
        {
            get => _bufferRegion.Start;
        }

        public BufferRegion Payload
        {
            get => _bufferRegion.Slice(
                        length: _bufferRegion.Length - 2 * sizeof(short),
                        start: sizeof(short));
        }

        public AllocationBlock? PreviousBlock
        {
            get
            {
                if (Start <= 2 * sizeof(short))
                {
                    return null;
                }

                var previousBlockLength = ReadBlockSize(_bufferRegion.GetLeftAdjacentRegion(sizeof(short)));
                return new AllocationBlock(_bufferRegion.GetLeftAdjacentRegion(previousBlockLength));
            }
        }

        public AllocationBlock? NextBlock
        {
            get
            {
                if (Start + _bufferRegion.Length + 2 * sizeof(short) >= _bufferRegion.Buffer.Length)
                {
                    return null;
                }

                var nextBlockLength = ReadBlockSize(_bufferRegion.GetRightAdjacentRegion(sizeof(short)));
                return new AllocationBlock(_bufferRegion.GetRightAdjacentRegion(nextBlockLength));
            }
        }

        public AllocationBlock JoinWith(AllocationBlock nextBlock)
        {
            Verify.That(IsFree == nextBlock.IsFree);
            return SetupBlock(_bufferRegion.JoinWith(nextBlock._bufferRegion), IsFree);
        }

        public (AllocationBlock, AllocationBlock) Split(short size)
        {
            Verify.That(Payload.Length >= size + 4 * sizeof(short));
            var isFree = IsFree;
            var (firstRegion, secondRegion) = _bufferRegion.Split(Cast.ToShort(2 * sizeof(short) + size));
            return (SetupBlock(firstRegion, isFree), SetupBlock(secondRegion, isFree));
        }

        public void MarkAsUsed()
        {
            Verify.That(IsFree);
            MarkAs(used: true);
        }

        public FreeBlock MarkAsFree()
        {
            Verify.That(!IsFree);
            MarkAs(used: false);
            var result = new FreeBlock(this);
            result.NextFreeBlockReference.Reset();
            return result;
        }

        public override string ToString() => $"{(IsFree ? '+' : '-')}[{Start}-{End}/{Length}]";

        public static AllocationBlock? FromReference(MemoryReference memoryReference)
        {
            if (memoryReference.IsNull)
            {
                return null;
            }

            var memoryBuffer = memoryReference.Buffer;
            var blockStart = memoryReference.TargetLocationOffset.Value;
            var bufferLength = ReadBlockSize(new BufferRegion(memoryBuffer, regionStart: blockStart, regionLength: sizeof(short)));
            var bufferRegion = new BufferRegion(memoryBuffer, regionStart: blockStart, regionLength: bufferLength);
            return new AllocationBlock(bufferRegion);
        }

        public static AllocationBlock SetupBlock(BufferRegion bufferRegion, bool isFree)
        {
            var result = new AllocationBlock(bufferRegion);
            result.MarkAs(!isFree);
            return result;
        }

        public static short ReadBlockSize(BufferRegion sizeBufferRegion)
        {
            Assert.IsTrue(sizeBufferRegion.Length == sizeof(short));
            return Math.Abs(sizeBufferRegion.CoreReadShortAt(0));
        }

        private short LengthAtHead
        {
            get => _bufferRegion.CoreReadShortAt(0);
        }

        private short LengthAtTail
        {
            get => _bufferRegion.CoreReadShortAt(Cast.ToShort(_bufferRegion.Length - sizeof(short)));
        }

        private short Length
        {
            get => Math.Abs(LengthAtHead);
        }

        private int End
        {
            get => Start + Length;
        }

        private void MarkAs(bool used)
        {
            var length = Cast.ToShort(used ? -_bufferRegion.Length : _bufferRegion.Length);
            _bufferRegion.CoreWriteAt(offset: 0, value: length);
            _bufferRegion.CoreWriteAt(offset: Cast.ToShort(_bufferRegion.Length - sizeof(short)), value: length);
        }

        private readonly BufferRegion _bufferRegion;
    }


    public readonly struct FreeBlock
    {
        public FreeBlock(AllocationBlock allocationBlock)
        {
            Assert.IsTrue(allocationBlock.IsFree);
            AllocationBlock = allocationBlock;
        }

        internal short Start
        {
            get => AllocationBlock.Start;
        }

        public int Length
        {
            get => AllocationBlock.Payload.Length;
        }

        public FreeBlock? NextFreeBlock
        {
            get => FromReference(NextFreeBlockReference);
        }

        public BufferRegion Use()
        {
            AllocationBlock.MarkAsUsed();
            Payload.SetContent(0);
            return Payload;
        }

        public FreeBlock JoinWith(FreeBlock nextFreeBlock)
        {
            var nextFreeBlockReference = nextFreeBlock.NextFreeBlockReference;
            var result = new FreeBlock(AllocationBlock.JoinWith(nextFreeBlock.AllocationBlock));
            result.NextFreeBlockReference.Set(nextFreeBlockReference);
            return result;
        }

        public (FreeBlock, FreeBlock) Split(short size)
        {
            var (firstBlock, secondBlock) = AllocationBlock.Split(size);
            var firstFreeBlock = new FreeBlock(firstBlock);
            var secondFreeBlock = new FreeBlock(secondBlock);
            secondFreeBlock.NextFreeBlockReference.Set(NextFreeBlockReference);
            firstFreeBlock.NextFreeBlockReference.Set(secondBlock);
            return (firstFreeBlock, secondFreeBlock);
        }

        public override string ToString() => $"[{Payload.Start}-{Payload.End}/{Length}]=>{NextFreeBlockReference.TargetLocationOffset};";

        internal BufferRegion Payload { get => AllocationBlock.Payload; }

        internal MemoryReference NextFreeBlockReference
        {
            get => new MemoryReference(AllocationBlock.Payload.Slice(0, sizeof(short)));
        }

        internal static FreeBlock? FromReference(MemoryReference memoryReference)
        {
            var allocationBlock = AllocationBlock.FromReference(memoryReference);
            return allocationBlock == null ? (FreeBlock?)null : new FreeBlock(allocationBlock.Value);
        }

        internal readonly AllocationBlock AllocationBlock;
    }


    public readonly struct MemoryReference
    {
        public MemoryReference(BufferRegion referenceHoldingRegion)
        {
            Assert.IsTrue(referenceHoldingRegion.Length == sizeof(short));
            _referenceHoldingRegion = referenceHoldingRegion;
        }

        public bool IsNull
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => TargetLocationOffset == null;
        }

        public byte[] Buffer => _referenceHoldingRegion.Buffer;

        public short? TargetLocationOffset
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get
            {
                var result = _referenceHoldingRegion.CoreReadShortAt(0);
                return result < 0 ? (short?)null : result;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Set(AllocationBlock block)
        {
            _referenceHoldingRegion.CoreWriteAt(offset: 0, value: block.Start);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Set(MemoryReference reference)
        {
            _referenceHoldingRegion.CoreWriteAt(offset: 0, value: reference.TargetLocationOffset ?? -1);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Reset()
        {
            _referenceHoldingRegion.CoreWriteAt(offset: 0, value: -1);
        }

        private readonly BufferRegion _referenceHoldingRegion;
    }
}
