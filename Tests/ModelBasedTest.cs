using FsCheck;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

namespace Tests
{
    [TestClass]
    public class ModelBasedTest
    {
        private MemoryAllocator _memoryAllocator;
        private List<BufferRegion> _allocatedRegions;

        private void Allocate(short size)
        {
            var allocatedBlock = _memoryAllocator.Allocate(size).Value;
            Trace.WriteLine($"Allocate({size}) -> {allocatedBlock}");
            _allocatedRegions.Add(allocatedBlock);
            Trace.WriteLine(_memoryAllocator);
        }

        private void Deallocate(int allocatedRegionIndex)
        {
            allocatedRegionIndex = allocatedRegionIndex % _allocatedRegions.Count;
            var allocatedRegion = _allocatedRegions[allocatedRegionIndex];
            Trace.WriteLine($"Deallocate({allocatedRegion})... ");
            _memoryAllocator.Deallocate(allocatedRegion);
            _allocatedRegions.RemoveAt(allocatedRegionIndex);
            Trace.WriteLine(_memoryAllocator);
        }

        #region Tests
        [TestMethod]
        [Ignore]
        public void Experiment()
        {
            _memoryAllocator = new MemoryAllocator();
            _allocatedRegions = new List<BufferRegion>();

            Trace.WriteLine(_memoryAllocator);

            var pattern = new[] { FreeBlocksJoinType.JoinWithRightBlock, FreeBlocksJoinType.JoinWithLeftAndRightBlocks, FreeBlocksJoinType.JoinWithRightBlock };
            var appliedPattern = ApplyPattern(pattern, _memoryAllocator);

            foreach (var freeBlocksJoinPerformer in appliedPattern)
            {
                foreach (var region in freeBlocksJoinPerformer.RegionsToDeallocate)
                {
                    _memoryAllocator.Deallocate(region);
                }
            }

            var expectedFreeBulocks = appliedPattern.Select(freeBlocksJoinPerformer => freeBlocksJoinPerformer.ExpectedJoinedRegion).ToList();
            var actualFreeBlocks = ListAllFreeBlocks(_memoryAllocator)
                        .Select(freeBlock => freeBlock.Payload)
                        .Take(appliedPattern.Count)
                        .ToList();
            Trace.WriteLine(_memoryAllocator);
            CollectionAssert.AreEqual(expectedFreeBulocks, actualFreeBlocks);
        }

        [TestMethod]
        public void ThereShouldBeNoIntersectionsBetweenAllocatedMemoryRegions()
        {
            CheckAllocatorBehaviour((_, allocatedBlocksChange) =>
                allocatedBlocksChange.AllocatedBlocks
                    .SelectMany(
                        currentRegion => allocatedBlocksChange.AllocatedBlocks.Except(new[] { currentRegion }),
                        (region1, region2) => AreIntersected(region1, region2))
                    .All(intersected => !intersected));
        }

        [TestMethod]
        public void AlteringContentOfAllocatedMemoryShouldNotChangeAllocatorState()
        {
            CheckAllocatorBehaviour((allocator, allocatedBlocksChange) =>
            {
                var freeBlocks = ListAllFreeBlocks(allocator);
                for (int v = byte.MinValue; v <= byte.MaxValue; v += 23)
                {
                    foreach (var region in allocatedBlocksChange.AllocatedBlocks)
                    {
                        region.SetContent((byte)v);
                    }

                    if (!freeBlocks.SequenceEqual(ListAllFreeBlocks(allocator)))
                    {
                        return false;
                    }
                }

                return true;
            });
        }

        [TestMethod]
        public void MemoryFromFreedRegionsShouldBeAvailableForAllocationAgain()
        {
            CheckAllocatorBehaviour((allocator, allocatedBlocksChange) =>
            {
                return
                    allocatedBlocksChange.RequestType == AllocatorRequestType.Allocate ||
                    ListAllFreeBlocks(allocator).Any(freeBlock => FullyContains(freeBlock, allocatedBlocksChange.ChangedRegion));
            });
        }

        [TestMethod]
        public void ThereShouldBeNoFreeBlocksWithLengthLessThan()
        {
            CheckAllocatorBehaviour((allocator, _) =>
                ListAllFreeBlocks(allocator).All(freeBlock => freeBlock.Length >= 2 * sizeof(short)));
        }

        [TestMethod]
        public void AdjacentFreeBlocksShouldBeMergedIntoSingleBlock()
        {
            Prop
                .ForAll(
                    Gen
                        .ListOf(Gen.Elements(Enum.GetValues(typeof(FreeBlocksJoinType)).Cast<FreeBlocksJoinType>()))
                        .Where(list => list.Length >= 3)
                        .ToArbitrary(),
                    pattern =>
                    {
                        var allocator = new MemoryAllocator();
                        var appliedPattern = ApplyPattern(pattern, allocator);

                        foreach (var freeBlocksJoinPerformer in appliedPattern)
                        {
                            foreach (var region in freeBlocksJoinPerformer.RegionsToDeallocate)
                            {
                                allocator.Deallocate(region);
                            }
                        }

                        return appliedPattern
                                .Select(freeBlocksJoinPerformer => freeBlocksJoinPerformer.ExpectedJoinedRegion)
                                .SequenceEqual(
                                    ListAllFreeBlocks(allocator)
                                    .Select(freeBlock => freeBlock.Payload)
                                    .Take(appliedPattern.Count));
                    })
                .QuickCheckThrowOnFailure();
        }

        [TestMethod]
        public void ReallocationShouldBeEquivalentToDeallocationFollowedByAllocation()
        {
            var foo = Gen.zip(
                        Gen.Elements(new[] { AllocatorRequestType.Allocate, AllocatorRequestType.Deallocate, AllocatorRequestType.Reallocate }),
                        Gen.Choose(1, short.MaxValue).Select(value => Cast.ToShort(value)));

        }

        [TestMethod]
        public void ReallocationShouldPreserveContentOfReallocatedBlock()
        {
            Assert.Fail();
        }

        [TestMethod]
        public void ReallocationShouldTryToWidenAllocationBlockWhenPossible()
        {
            Assert.Fail();
        }

        void CheckAllocatorBehaviour(Func<MemoryAllocator, AllocatedBlocksChange, bool> checkAllocatorInvariant)
        {
            new AllocatorCommandGenerator((a, rs) => checkAllocatorInvariant(a, rs).ToProperty()).ToProperty().QuickCheckThrowOnFailure();
        }

        class AllocatorCommandGenerator : ICommandGenerator<(MemoryAllocator, AllocatedBlocksChange), int>
        {
            public AllocatorCommandGenerator(Func<MemoryAllocator, AllocatedBlocksChange, Property> checkAllocatorInvariant)
            {
                _checkAllocatorInvariant = checkAllocatorInvariant;
            }

            public (MemoryAllocator, AllocatedBlocksChange) InitialActual
            {
                get => (
                        new MemoryAllocator(),
                        new AllocatedBlocksChange(
                              new List<BufferRegion>(),
                            AllocatorRequestType.Allocate,
                            new BufferRegion())
                        );
            }

            public int InitialModel
            {
                get => 0;
            }

            public Gen<Command<(MemoryAllocator, AllocatedBlocksChange), int>> Next(int numberOfAllocatedBlocks) =>
                from commandData in Gen.zip(
                            Gen.Elements(new[] { AllocatorRequestType.Allocate, AllocatorRequestType.Deallocate, AllocatorRequestType.Reallocate }),
                            Gen.Choose(1, short.MaxValue).Select(value => Cast.ToShort(value)))
                select (Command<(MemoryAllocator, AllocatedBlocksChange), int>)new AllocatorCommand(
                            commandData.Item1,
                            commandData.Item2,
                            _checkAllocatorInvariant);

            private readonly Func<MemoryAllocator, AllocatedBlocksChange, Property> _checkAllocatorInvariant;
        }

        class AllocatorCommand : Command<(MemoryAllocator, AllocatedBlocksChange), int>
        {
            public AllocatorCommand(
                AllocatorRequestType requestType,
                short requestParameter,
                Func<MemoryAllocator, AllocatedBlocksChange, Property> checkAllocatorInvariant)
            {
                _requestType = requestType;
                _requestParameter = requestParameter;
                _checkAllocatorInvariant = checkAllocatorInvariant;
            }

            public override bool Pre(int numberOfAllocatedBlocks) =>
                    _requestType == AllocatorRequestType.Allocate ||
                    ((_requestType == AllocatorRequestType.Deallocate || _requestType == AllocatorRequestType.Reallocate) && numberOfAllocatedBlocks > 0);

            public override (MemoryAllocator, AllocatedBlocksChange) RunActual(
                (MemoryAllocator, AllocatedBlocksChange) allocatorAndAllocatedBlocks)
            {
                var (allocator, allocatedBlocksChange) = allocatorAndAllocatedBlocks;
                switch (_requestType)
                {
                    case AllocatorRequestType.Allocate:
                        {
                            var allocatedBlock = allocator.Allocate(Cast.ToShort(Math.Max(1, _requestParameter / 100)));
                            if (allocatedBlock == null && CalculateUsedSpace(allocatedBlocksChange.AllocatedBlocks) + _requestParameter > 30 * 1024)
                            {
                                return allocatorAndAllocatedBlocks;
                            }

                            var blocks = allocatedBlocksChange.AllocatedBlocks.ToList();
                            blocks.Add(allocatedBlock.Value);
                            return (allocator, new AllocatedBlocksChange(blocks, AllocatorRequestType.Allocate, allocatedBlock.Value));
                        }

                    case AllocatorRequestType.Deallocate:
                        {
                            var indexOfBlockToDeallocate = _requestParameter % allocatedBlocksChange.AllocatedBlocks.Count;
                            var deallocatedBlock = allocatedBlocksChange.AllocatedBlocks[indexOfBlockToDeallocate];
                            allocator.Deallocate(deallocatedBlock);
                            var blocks = allocatedBlocksChange.AllocatedBlocks.ToList();
                            blocks.RemoveAt(indexOfBlockToDeallocate);
                            return (allocator, new AllocatedBlocksChange(blocks, AllocatorRequestType.Deallocate, deallocatedBlock));
                        }

                    case AllocatorRequestType.Reallocate:
                        {
                            var indexOfBlockToReallocate = _requestParameter % allocatedBlocksChange.AllocatedBlocks.Count;
                            var blockToReallocate = allocatedBlocksChange.AllocatedBlocks[indexOfBlockToReallocate];
                            var reallocatedBlock = allocator.Reallocate(blockToReallocate, );
                            var blocks = allocatedBlocksChange.AllocatedBlocks.ToList();
                            blocks[indexOfBlockToReallocate] = reallocatedBlock;
                            return (allocator, new AllocatedBlocksChange(blocks, AllocatorRequestType.Reallocate, reallocatedBlock));
                        }

                    default:
                        throw new NotSupportedException($"AllocatorRequestType {_requestType} is not supported.");
                }
            }

            public override int RunModel(int numberOfAllocatedBlocks)
            {
                switch (_requestType)
                {
                    case AllocatorRequestType.Allocate:
                        return numberOfAllocatedBlocks + 1;

                    case AllocatorRequestType.Deallocate:
                        return numberOfAllocatedBlocks - 1;

                    case AllocatorRequestType.Reallocate:
                        return numberOfAllocatedBlocks;

                    default:
                        throw new NotSupportedException($"AllocatorRequestType {_requestType} is not supported.");
                }
            }

            public override Property Post(
                (MemoryAllocator, AllocatedBlocksChange) allocatorAndAllocatedBlocks,
                int numberOfAllocatedBlocks)
            {
                var (allocator, allocatedBlocks) = allocatorAndAllocatedBlocks;
                return _checkAllocatorInvariant(allocator, allocatedBlocks);
            }

            public override string ToString() => $"{_requestType}({_requestParameter})";

            private int CalculateUsedSpace(IEnumerable<BufferRegion> allocatedBlocks)
            {
                return allocatedBlocks.Sum(block => block.Length + 2 * sizeof(short));
            }

            private readonly AllocatorRequestType _requestType;
            private readonly short _requestParameter;
            private readonly Func<MemoryAllocator, AllocatedBlocksChange, Property> _checkAllocatorInvariant;
        }

        readonly struct AllocatedBlocksChange
        {
            public AllocatedBlocksChange(
                IReadOnlyList<BufferRegion> allocatedBlocks,
                AllocatorRequestType requestType,
                BufferRegion changedRegion)
            {
                AllocatedBlocks = allocatedBlocks;
                RequestType = requestType;
                ChangedRegion = changedRegion;
            }

            public readonly IReadOnlyList<BufferRegion> AllocatedBlocks;

            public readonly AllocatorRequestType RequestType;

            public readonly BufferRegion ChangedRegion;
        }

        enum AllocatorRequestType { Allocate, Deallocate, Reallocate }

        static IEnumerable<FreeBlock> ListAllFreeBlocks(MemoryAllocator allocator)
        {
            for (var freeBlock = allocator.FirstFreeBlock; freeBlock != null;)
            {
                yield return freeBlock.Value;
                freeBlock = freeBlock.Value.NextFreeBlock;
            }
        }


        static bool AreIntersected(BufferRegion r1, BufferRegion r2)
        {
            Verify.That(ReferenceEquals(r1.Buffer, r2.Buffer));
            return IsBetween(r1.Start, r2.Start, Cast.ToShort(r2.Start + r2.Length)) ||
                   IsBetween(Cast.ToShort(r1.Start + r1.Length), r2.Start, Cast.ToShort(r2.Start + r2.Length));
        }

        static bool FullyContains(FreeBlock block, BufferRegion region)
        {
            var blockPayload = block.Payload;
            Verify.That(ReferenceEquals(blockPayload.Buffer, region.Buffer));
            return IsBetween(region.Start, blockPayload.Start, Cast.ToShort(blockPayload.Start + blockPayload.Length)) &&
                   IsBetween(Cast.ToShort(region.Start + region.Length), blockPayload.Start, Cast.ToShort(blockPayload.Start + blockPayload.Length));
        }

        static bool IsBetween(short p, short s1, short s2)
        {
            return s1 <= p && p <= s2;
        }

        enum FreeBlocksJoinType { JoinWithRightBlock, JoinWithLeftBlock, JoinWithLeftAndRightBlocks }

        readonly struct FreeBlocksJoinPerformer
        {
            public FreeBlocksJoinPerformer(
                FreeBlocksJoinType joinType,
                MemoryAllocator allocator,
                System.Random memoryBlockSizeGenerator)
            {
                var memoryBlockSet = Enumerable
                    .Range(0, 5)
                    .Select(_ => allocator.Allocate(Cast.ToShort(memoryBlockSizeGenerator.Next(1, 64))).Value)
                    .ToArray();

                switch (joinType)
                {
                    case FreeBlocksJoinType.JoinWithRightBlock:
                        RegionsToDeallocate = new[] { memoryBlockSet[3], memoryBlockSet[2] };
                        ExpectedJoinedRegion = Join(memoryBlockSet[2], memoryBlockSet[3]);
                        break;

                    case FreeBlocksJoinType.JoinWithLeftBlock:
                        RegionsToDeallocate = new[] { memoryBlockSet[1], memoryBlockSet[2] };
                        ExpectedJoinedRegion = Join(memoryBlockSet[1], memoryBlockSet[2]);
                        break;

                    case FreeBlocksJoinType.JoinWithLeftAndRightBlocks:
                        RegionsToDeallocate = new[] { memoryBlockSet[1], memoryBlockSet[3], memoryBlockSet[2] };
                        ExpectedJoinedRegion = Join(memoryBlockSet[1], memoryBlockSet[2], memoryBlockSet[3]);
                        break;

                    default:
                        throw new ArgumentException("joinType");
                }
            }

            public readonly IReadOnlyCollection<BufferRegion> RegionsToDeallocate;

            public readonly BufferRegion ExpectedJoinedRegion;

            private static BufferRegion Join(BufferRegion region1, BufferRegion region2, BufferRegion? region3 = null)
            {
                Assert.IsTrue(region1.End + 2 * sizeof(short) == region2.Start);
                if (region3 != null)
                {
                    Assert.IsTrue(region2.End + 2 * sizeof(short) == region3.Value.Start);
                }

                return region3 == null 
                    ? new BufferRegion(region1.Buffer, regionStart: region1.Start, regionLength: region1.Length + 2 * sizeof(short) + region2.Length)
                    : new BufferRegion(region1.Buffer, regionStart: region1.Start, regionLength: region1.Length + 2 * sizeof(short) + region2.Length + 2 * sizeof(short) + region3.Value.Length);
            }
        }

        private static IReadOnlyCollection<FreeBlocksJoinPerformer> ApplyPattern(
            IEnumerable<FreeBlocksJoinType> pattern,
            MemoryAllocator allocator)
        {
            var memoryBlockSizeGenerator = new System.Random();
            return pattern.Select(joinType => new FreeBlocksJoinPerformer(joinType, allocator, memoryBlockSizeGenerator)).AsImmutable();
        }

        #endregion Tests
    }

    static class CollectionExtensions
    {
        public static IReadOnlyCollection<T> AsImmutable<T>(this IEnumerable<T> @this) =>
            (@this as IReadOnlyCollection<T>) ?? @this.ToArray();
    }
}
