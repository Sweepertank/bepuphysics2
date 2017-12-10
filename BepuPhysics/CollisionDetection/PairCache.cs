﻿using BepuUtilities;
using BepuUtilities.Collections;
using BepuUtilities.Memory;
using BepuPhysics.Collidables;
using BepuPhysics.Constraints;
using BepuPhysics.Constraints.Contact;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using static BepuPhysics.CollisionDetection.WorkerPairCache;

namespace BepuPhysics.CollisionDetection
{
    //would you care for some generics
    using OverlapMapping = QuickDictionary<CollidablePair, CollidablePairPointers, Buffer<CollidablePair>, Buffer<CollidablePairPointers>, Buffer<int>, CollidablePairComparer>;

    [StructLayout(LayoutKind.Explicit, Size = 8)]
    public struct CollidablePair
    {
        [FieldOffset(0)]
        public CollidableReference A;
        [FieldOffset(4)]
        public CollidableReference B;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public CollidablePair(CollidableReference a, CollidableReference b)
        {
            A = a;
            B = b;
        }

        public override string ToString()
        {
            return $"<{A.Mobility}[{A.Handle}], {B.Mobility}[{B.Handle}]>";
        }
    }

    public struct CollidablePairComparer : IEqualityComparerRef<CollidablePair>
    {
        //Note that pairs are sorted by handle, so we can assume order matters.
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool Equals(ref CollidablePair a, ref CollidablePair b)
        {
            return Unsafe.As<CollidablePair, ulong>(ref a) == Unsafe.As<CollidablePair, ulong>(ref b);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int Hash(ref CollidablePair item)
        {
            const ulong p1 = 961748927UL;
            const ulong p2 = 899809343UL;
            var hash64 = (ulong)item.A.Packed * (p1 * p2) + (ulong)item.B.Packed * (p2);
            return (int)(hash64 ^ (hash64 >> 32));
        }
    }

    public struct CollidablePairPointers
    {
        /// <summary>
        /// A narrowphase-specific type and index into the pair cache's constraint data set. Collision pairs which have no associated constraint, either 
        /// because no contacts were generated or because the constraint was filtered, will have a nonexistent ConstraintCache.
        /// </summary>
        public PairCacheIndex ConstraintCache;
        /// <summary>
        /// A narrowphase-specific type and index into a batch of custom data for the pair. Many types do not use any supplementary data, but some make use of temporal coherence
        /// to accelerate contact generation.
        /// </summary>
        public PairCacheIndex CollisionDetectionCache;
    }


    public class PairCache
    {
        public OverlapMapping Mapping;

        /// <summary>
        /// Per-pair 'freshness' flags set when a pair is added or updated by the narrow phase execution. Only initialized for the duration of the narrowphase's execution.
        /// </summary>
        /// <remarks>
        /// This stores one byte per pair. While it could be compressed to 1 bit, that requires manually ensuring thread safety. By using bytes, we rely on the 
        /// atomic setting behavior for data types no larger than the native pointer size. Further, smaller sizes actually pay a higher price in terms of increased false sharing.
        /// Choice of data type is a balancing act between the memory bandwidth of the post analysis and the frequency of false sharing.
        /// </remarks>
        internal RawBuffer PairFreshness;
        BufferPool pool;
        int minimumPendingSize;
        int minimumPerTypeCapacity;
        int previousPendingSize;

        //While the current worker caches are read from, the next caches are written to.
        //The worker pair caches contain a reference to a buffer pool, which is a reference type. That makes WorkerPairCache non-blittable, so in the interest of not being
        //super duper gross, we don't use the untyped buffer pools to store it. 
        //Given that the size of the arrays here will be small and almost never change, this isn't a significant issue.
        QuickList<WorkerPairCache, Array<WorkerPairCache>> workerCaches;
        internal QuickList<WorkerPairCache, Array<WorkerPairCache>> NextWorkerCaches;


        public PairCache(BufferPool pool, int initialSetCapacity, int minimumMappingSize, int minimumPendingSize, int minimumPerTypeCapacity)
        {
            this.minimumPendingSize = minimumPendingSize;
            this.minimumPerTypeCapacity = minimumPerTypeCapacity;
            this.pool = pool;
            OverlapMapping.Create(
                pool.SpecializeFor<CollidablePair>(), pool.SpecializeFor<CollidablePairPointers>(), pool.SpecializeFor<int>(),
                SpanHelper.GetContainingPowerOf2(minimumMappingSize), 3, out Mapping);
            ResizeSetsCapacity(initialSetCapacity, 0);
        }

        public void Prepare(IThreadDispatcher threadDispatcher = null)
        {
            int maximumConstraintTypeCount = 0, maximumCollisionTypeCount = 0;
            for (int i = 0; i < workerCaches.Count; ++i)
            {
                workerCaches[i].GetMaximumCacheTypeCounts(out var collision, out var constraint);
                if (collision > maximumCollisionTypeCount)
                    maximumCollisionTypeCount = collision;
                if (constraint > maximumConstraintTypeCount)
                    maximumConstraintTypeCount = constraint;
            }
            QuickList<PreallocationSizes, Buffer<PreallocationSizes>>.Create(pool.SpecializeFor<PreallocationSizes>(), maximumConstraintTypeCount, out var minimumSizesPerConstraintType);
            QuickList<PreallocationSizes, Buffer<PreallocationSizes>>.Create(pool.SpecializeFor<PreallocationSizes>(), maximumCollisionTypeCount, out var minimumSizesPerCollisionType);
            //Since the minimum size accumulation builds the minimum size incrementally, bad data within the array can corrupt the result- we must clear it.
            minimumSizesPerConstraintType.Span.Clear(0, minimumSizesPerConstraintType.Span.Length);
            minimumSizesPerCollisionType.Span.Clear(0, minimumSizesPerCollisionType.Span.Length);
            for (int i = 0; i < workerCaches.Count; ++i)
            {
                workerCaches[i].AccumulateMinimumSizes(ref minimumSizesPerConstraintType, ref minimumSizesPerCollisionType);
            }

            var threadCount = threadDispatcher != null ? threadDispatcher.ThreadCount : 1;
            //Ensure that the new worker pair caches can hold all workers.
            if (!NextWorkerCaches.Span.Allocated || NextWorkerCaches.Span.Length < threadCount)
            {
                //The next worker caches should never need to be disposed here. The flush should have taken care of it.
#if DEBUG
                for (int i = 0; i < NextWorkerCaches.Count; ++i)
                    Debug.Assert(NextWorkerCaches[i].Equals(default(WorkerPairCache)));
#endif
                QuickList<WorkerPairCache, Array<WorkerPairCache>>.Create(new PassthroughArrayPool<WorkerPairCache>(), threadCount, out NextWorkerCaches);
            }
            //Note that we have not initialized the workerCaches from the previous frame. In the event that this is the first frame and there are no previous worker caches,
            //there will be no pointers into the caches, and removal analysis loops over the count which defaults to zero- so it's safe.
            NextWorkerCaches.Count = threadCount;

            var pendingSize = Math.Max(minimumPendingSize, previousPendingSize);
            if (threadDispatcher != null)
            {
                for (int i = 0; i < threadCount; ++i)
                {
                    NextWorkerCaches[i] = new WorkerPairCache(i, threadDispatcher.GetThreadMemoryPool(i), ref minimumSizesPerConstraintType, ref minimumSizesPerCollisionType,
                        pendingSize, minimumPerTypeCapacity);
                }
            }
            else
            {
                NextWorkerCaches[0] = new WorkerPairCache(0, pool, ref minimumSizesPerConstraintType, ref minimumSizesPerCollisionType, pendingSize, minimumPerTypeCapacity);
            }
            minimumSizesPerConstraintType.Dispose(pool.SpecializeFor<PreallocationSizes>());
            minimumSizesPerCollisionType.Dispose(pool.SpecializeFor<PreallocationSizes>());

            //Create the pair freshness array for the existing overlaps.
            pool.Take(Mapping.Count, out PairFreshness);
            //This clears 1 byte per pair. 32768 pairs with 10GBps assumed single core bandwidth means about 3 microseconds.
            //There is a small chance that multithreading this would be useful in larger simulations- but it would be very, very close.
            PairFreshness.Clear(0, Mapping.Count);

        }


        internal void EnsureConstraintToPairMappingCapacity(Solver solver, int targetCapacity)
        {
            targetCapacity = Math.Max(solver.HandlePool.HighestPossiblyClaimedId + 1, targetCapacity);
            if (ConstraintHandleToPair.Length < targetCapacity)
            {
                pool.SpecializeFor<CollisionPairLocation>().Resize(ref ConstraintHandleToPair, targetCapacity, ConstraintHandleToPair.Length);
            }
        }

        internal void ResizeConstraintToPairMappingCapacity(Solver solver, int targetCapacity)
        {
            targetCapacity = BufferPool<CollisionPairLocation>.GetLowestContainingElementCount(Math.Max(solver.HandlePool.HighestPossiblyClaimedId + 1, targetCapacity));
            if (ConstraintHandleToPair.Length != targetCapacity)
            {
                pool.SpecializeFor<CollisionPairLocation>().Resize(ref ConstraintHandleToPair, targetCapacity, Math.Min(targetCapacity, ConstraintHandleToPair.Length));
            }
        }



        /// <summary>
        /// Flush all deferred changes from the last narrow phase execution.
        /// </summary>
        public void PrepareFlushJobs(ref QuickList<NarrowPhaseFlushJob, Buffer<NarrowPhaseFlushJob>> jobs)
        {
            //Get rid of the now-unused worker caches.
            for (int i = 0; i < workerCaches.Count; ++i)
            {
                workerCaches[i].Dispose();
            }

            //The freshness cache should have already been used in order to generate the constraint removal requests and the PendingRemoves that we handle in a moment; dispose it now.
            pool.Return(ref PairFreshness);

            //Ensure the overlap mapping size is sufficient up front. This requires scanning all the pending sizes.
            int largestIntermediateSize = Mapping.Count;
            var newMappingSize = Mapping.Count;
            for (int i = 0; i < NextWorkerCaches.Count; ++i)
            {
                ref var cache = ref NextWorkerCaches[i];
                //Removes occur first, so this cache can only result in a larger mapping if there are more adds than removes.
                newMappingSize += cache.PendingAdds.Count - cache.PendingRemoves.Count;
                if (newMappingSize > largestIntermediateSize)
                    largestIntermediateSize = newMappingSize;
            }
            Mapping.EnsureCapacity(largestIntermediateSize, pool.SpecializeFor<CollidablePair>(), pool.SpecializeFor<CollidablePairPointers>(), pool.SpecializeFor<int>());

            jobs.Add(new NarrowPhaseFlushJob { Type = NarrowPhaseFlushJobType.FlushPairCacheChanges }, pool.SpecializeFor<NarrowPhaseFlushJob>());
        }
        public unsafe void FlushMappingChanges()
        {
            //Flush all pending adds from the new set.
            //Note that this phase accesses no shared memory- it's all pair cache local, and no pool accesses are made.
            //That means we could run it as a job alongside solver constraint removal. That's good, because adding and removing to the hash tables isn't terribly fast.  
            //(On the order of 10-100 nanoseconds per operation, so in pathological cases, it can start showing up in profiles.)
            for (int i = 0; i < NextWorkerCaches.Count; ++i)
            {
                ref var cache = ref NextWorkerCaches[i];

                //Walk backwards on the off chance that a swap can be avoided.
                for (int j = cache.PendingRemoves.Count - 1; j >= 0; --j)
                {
                    var removed = Mapping.FastRemove(ref cache.PendingRemoves[j]);
                    Debug.Assert(removed);
                }
                for (int j = 0; j < cache.PendingAdds.Count; ++j)
                {
                    ref var pending = ref cache.PendingAdds[j];
                    var added = Mapping.AddUnsafely(ref pending.Pair, ref pending.Pointers);
                    Debug.Assert(added);
                }
            }
        }
        public void Postflush()
        {
            //This bookkeeping and disposal phase is trivially cheap compared to the cost of updating the mapping table, so we do it sequentially.
            //The fact that we access the per-worker pools here would prevent easy multithreading anyway; the other threads may use them. 
            int largestPendingSize = 0;
            for (int i = 0; i < NextWorkerCaches.Count; ++i)
            {
                ref var cache = ref NextWorkerCaches[i];
                if (cache.PendingAdds.Count > largestPendingSize)
                {
                    largestPendingSize = cache.PendingAdds.Count;
                }
                if (cache.PendingRemoves.Count > largestPendingSize)
                {
                    largestPendingSize = cache.PendingRemoves.Count;
                }
                cache.PendingAdds.Dispose(cache.pool.SpecializeFor<PendingAdd>());
                cache.PendingRemoves.Dispose(cache.pool.SpecializeFor<CollidablePair>());
            }
            previousPendingSize = largestPendingSize;

            //Swap references.
            var temp = workerCaches;
            workerCaches = NextWorkerCaches;
            NextWorkerCaches = temp;


        }

        internal void Clear()
        {
            for (int i = 0; i < workerCaches.Count; ++i)
            {
                workerCaches[i].Dispose();
            }
            workerCaches.Count = 0;
            for (int i = 1; i < InactiveSets.Length; ++i)
            {
                if (InactiveSets[i].Allocated)
                {
                    InactiveSets[i].Dispose(pool);
                }
            }
#if DEBUG
            if (NextWorkerCaches.Span.Allocated)
            {
                for (int i = 0; i < NextWorkerCaches.Span.Length; ++i)
                {
                    Debug.Assert(NextWorkerCaches[i].Equals(default(WorkerPairCache)), "Outside of the execution of the narrow phase, the 'next' caches should not be allocated.");
                }
            }
#endif
        }

        public void Dispose()
        {
            for (int i = 0; i < workerCaches.Count; ++i)
            {
                workerCaches[i].Dispose();
            }
            //Note that we do not need to dispose the worker cache arrays themselves- they were just arrays pulled out of a passthrough pool.
#if DEBUG
            if (NextWorkerCaches.Span.Allocated)
            {
                for (int i = 0; i < NextWorkerCaches.Span.Length; ++i)
                {
                    Debug.Assert(NextWorkerCaches[i].Equals(default(WorkerPairCache)), "Outside of the execution of the narrow phase, the 'next' caches should not be allocated.");
                }
            }
#endif
            Mapping.Dispose(pool.SpecializeFor<CollidablePair>(), pool.SpecializeFor<CollidablePairPointers>(), pool.SpecializeFor<int>());
            pool.SpecializeFor<InactivePairCache>().Return(ref InactiveSets);
            //The constraint handle to pair is partially slaved to the constraint handle capacity. 
            //It gets ensured every frame, but the gap between construction and the first frame could leave it uninitialized.
            if (ConstraintHandleToPair.Allocated)
                pool.SpecializeFor<CollisionPairLocation>().Return(ref ConstraintHandleToPair);
        }


        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int IndexOf(ref CollidablePair pair)
        {
            return Mapping.IndexOf(ref pair);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public ref CollidablePairPointers GetPointers(int index)
        {
            return ref Mapping.Values[index];
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal unsafe void FillNewConstraintCache<TConstraintCache>(int* featureIds, ref TConstraintCache cache)
        {
            //1 contact constraint caches do not store a feature id; it's pointless.
            if (typeof(TConstraintCache) == typeof(ConstraintCache2))
            {
                ref var typedCache = ref Unsafe.As<TConstraintCache, ConstraintCache2>(ref cache);
                typedCache.FeatureId0 = featureIds[0];
                typedCache.FeatureId1 = featureIds[1];
            }
            else if (typeof(TConstraintCache) == typeof(ConstraintCache3))
            {
                ref var typedCache = ref Unsafe.As<TConstraintCache, ConstraintCache3>(ref cache);
                typedCache.FeatureId0 = featureIds[0];
                typedCache.FeatureId1 = featureIds[1];
                typedCache.FeatureId2 = featureIds[2];
            }
            else if (typeof(TConstraintCache) == typeof(ConstraintCache4))
            {
                ref var typedCache = ref Unsafe.As<TConstraintCache, ConstraintCache4>(ref cache);
                typedCache.FeatureId0 = featureIds[0];
                typedCache.FeatureId1 = featureIds[1];
                typedCache.FeatureId2 = featureIds[2];
                typedCache.FeatureId3 = featureIds[3];
            }
            //TODO: In the event that higher contact count manifolds exist for the purposes of nonconvexes, this will need to be expanded.
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal unsafe PairCacheIndex Add<TConstraintCache, TCollisionCache>(int workerIndex, ref CollidablePair pair,
            ref TCollisionCache collisionCache, ref TConstraintCache constraintCache)
            where TConstraintCache : IPairCacheEntry
            where TCollisionCache : IPairCacheEntry
        {
            //Note that we do not have to set any freshness bytes here; using this path means there exists no previous overlap to remove anyway.
            return NextWorkerCaches[workerIndex].Add(ref pair, ref collisionCache, ref constraintCache);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal unsafe void Update<TConstraintCache, TCollisionCache>(int workerIndex, int pairIndex, ref CollidablePairPointers pointers,
            ref TCollisionCache collisionCache, ref TConstraintCache constraintCache)
            where TConstraintCache : IPairCacheEntry
            where TCollisionCache : IPairCacheEntry
        {
            //We're updating an existing pair, so we should prevent this pair from being removed.
            PairFreshness[pairIndex] = 0xFF;
            NextWorkerCaches[workerIndex].Update(ref pointers, ref collisionCache, ref constraintCache);
        }
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal unsafe void UpdateForExistingConstraint<TConstraintCache, TCollisionCache>(int workerIndex, int pairIndex, ref CollidablePairPointers pointers,
            ref TCollisionCache collisionCache, ref TConstraintCache constraintCache, int constraintHandle)
            where TConstraintCache : IPairCacheEntry
            where TCollisionCache : IPairCacheEntry
        {
            Update(workerIndex, pairIndex, ref pointers, ref collisionCache, ref constraintCache);
            //This codepath is used when an existing constraint can be updated without any need for an add/remove.
            //CompleteConstraintAdd updates the handle->pair mapping, but since there is no add for this pair, we have to update the mapping for the new constraint cache location immediately.
            //Note that the CollidablePair data doesn't need to be changed- for this codepath to be used, the pair and handle are unchanged.
            ref var mapping = ref ConstraintHandleToPair[constraintHandle];
            mapping.ConstraintCache = pointers.ConstraintCache;
        }


        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal int GetContactCount(int constraintType)
        {
            //TODO: Very likely that we'll expand the nonconvex manifold maximum to 8 contacts, so this will need to be adjusted later.
            return 1 + (constraintType & 0x3);
        }

        /// <summary>
        /// Gets whether a constraint type id maps to a contact constraint.
        /// </summary>
        /// <param name="constraintTypeId">Id of the constraint to check.</param>
        /// <returns>True if the type id refers to a contact constraint. False otherwise.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool IsContactBatch(int constraintTypeId)
        {
            //TODO: If the nonconvex contact count expands to 8, this will have to change.
            return constraintTypeId < 16;
        }

        internal struct CollisionPairLocation
        {
            public CollidablePair Pair;
            public PairCacheIndex ConstraintCache;
        }

        /// <summary>
        /// Mapping from constraint handle back to collision detection pair cache locations.
        /// </summary>
        internal Buffer<CollisionPairLocation> ConstraintHandleToPair;


        internal struct InactivePairCache
        {
            public bool Allocated { get { return constraintCaches.Allocated; } }

            internal Buffer<UntypedList> constraintCaches;
            internal Buffer<UntypedList> collisionCaches;

            internal void Dispose(BufferPool pool)
            {
                for (int i = 0; i < constraintCaches.Length; ++i)
                {
                    if (constraintCaches[i].Buffer.Allocated)
                        pool.Return(ref constraintCaches[i].Buffer);
                }
                pool.SpecializeFor<UntypedList>().Return(ref constraintCaches);
                for (int i = 0; i < collisionCaches.Length; ++i)
                {
                    if (collisionCaches[i].Buffer.Allocated)
                        pool.Return(ref collisionCaches[i].Buffer);
                }
                pool.SpecializeFor<UntypedList>().Return(ref collisionCaches);
                this = new InactivePairCache();
            }
        }
        //This buffer is filled in parallel with the Bodies.Sets and Solver.Sets.
        //Note that this does not include the active set, so index 0 is always empty.
        internal Buffer<InactivePairCache> InactiveSets;

        internal void ResizeSetsCapacity(int setsCapacity, int potentiallyAllocatedCount)
        {
            Debug.Assert(setsCapacity >= potentiallyAllocatedCount && potentiallyAllocatedCount <= InactiveSets.Length);
            setsCapacity = BufferPool<InactivePairCache>.GetLowestContainingElementCount(setsCapacity);
            if (InactiveSets.Length != setsCapacity)
            {
                var oldCapacity = InactiveSets.Length;
                pool.SpecializeFor<InactivePairCache>().Resize(ref InactiveSets, setsCapacity, potentiallyAllocatedCount);
                if (oldCapacity < InactiveSets.Length)
                    InactiveSets.Clear(oldCapacity, InactiveSets.Length - oldCapacity); //We rely on unused slots being default initialized.
            }
        }

        [Conditional("DEBUG")]
        internal unsafe void ValidateConstraintHandleToPairMapping()
        {
            ValidateConstraintHandleToPairMapping(ref workerCaches, false);
        }
        [Conditional("DEBUG")]
        internal unsafe void ValidateConstraintHandleToPairMappingInProgress(bool ignoreStale)
        {
            ValidateConstraintHandleToPairMapping(ref NextWorkerCaches, ignoreStale);
        }

        [Conditional("DEBUG")]
        internal unsafe void ValidateConstraintHandleToPairMapping(ref QuickList<WorkerPairCache, Array<WorkerPairCache>> caches, bool ignoreStale)
        {
            for (int i = 0; i < Mapping.Count; ++i)
            {
                if (!ignoreStale || PairFreshness[i] > 0)
                {
                    var existingCache = Mapping.Values[i].ConstraintCache;
                    var existingHandle = *(int*)(caches[existingCache.Cache].constraintCaches[existingCache.Type].Buffer.Memory + existingCache.Index);
                    Debug.Assert(existingCache.Active, "The overlap mapping should only contain references to constraints which are active.");
                    Debug.Assert(
                        ConstraintHandleToPair[existingHandle].ConstraintCache.packed == existingCache.packed &&
                        new CollidablePairComparer().Equals(ref ConstraintHandleToPair[existingHandle].Pair, ref Mapping.Keys[i]),
                        "The overlap mapping and handle mapping should match.");
                }
            }
        }

        [Conditional("DEBUG")]
        internal unsafe void ValidateHandleCountInMapping(int constraintHandle, int expectedCount)
        {
            int count = 0;
            for (int i = 0; i < Mapping.Count; ++i)
            {
                var existingCache = Mapping.Values[i].ConstraintCache;
                var existingHandle = *(int*)(workerCaches[existingCache.Cache].constraintCaches[existingCache.Type].Buffer.Memory + existingCache.Index);
                if (existingHandle == constraintHandle)
                {
                    ++count;
                    Debug.Assert(count <= expectedCount && count <= 1, "Expected count violated.");
                }
            }
            Debug.Assert(count == expectedCount, "Expected count for this handle not found!");
        }

        private unsafe void CopyToInactiveCache(int setIndex, ref Buffer<UntypedList> sourceCaches, ref Buffer<UntypedList> targetCaches, ref PairCacheIndex location)
        {
            var type = location.Type;
            ref var source = ref sourceCaches[type];
            //TODO: Last second allocations here again. A prepass would help determine a minimal size without doing a bunch of resizes.
            if (type >= targetCaches.Length)
            {
                var oldCapacity = targetCaches.Length;
                pool.SpecializeFor<UntypedList>().Resize(ref targetCaches, type + 1, targetCaches.Length);
                targetCaches.Clear(oldCapacity, targetCaches.Length - oldCapacity);
            }
            ref var target = ref targetCaches[type];

            //TODO: This is a pretty poor estimate, but produces minimal allocations. Given that many islands really do just involve
            //a single body, this isn't quite as absurd as it looks. However, you may want to consider walking the handles ahead of time to preallocate exactly enough space.
            var targetByteIndex = target.Allocate(source.ElementSizeInBytes, 1, pool);
            var sourceAddress = source.Buffer.Memory + location.Index;
            var targetAddress = target.Buffer.Memory + targetByteIndex;
            //TODO: These small copies are potentially a poor use case for cpblk, may want to examine.
            Unsafe.CopyBlockUnaligned(targetAddress, sourceAddress, (uint)source.ElementSizeInBytes);
            //The inactive set now contains both caches. Point the handle mapping at it.
            //Note the special encoding for inactive set entries. This doesn't affect the performance of active caches; they don't have to consider the activity state.
            //This is strictly for the benefit of the handle->cache lookup table. Further, note that *this is not actually required by the engine*.
            //Nowhere do we use the handle->cache lookup during normal execution. Since the handle->cache table allocation exists regardless, we take advantage of it to theoretically allow
            //a lookup of any extant collision detection information associated with a constraint. In other words, you can go:
            //body handle->constraint lists->constraint handle->pair caches lookup->collision/constraint caches.
            //Whoever is using this feature would need to have type knowledge of some sort to extract the information, but it costs us nothing to stick the information in there.
            //If we end up not wanting to support this, all you have to do is remove this line. You don't even have to clear the slot.
            //(Note that we DO require that the handle mapping contains a valid pair. Activation relies on the handle mapping stored pair.)
            location = PairCacheIndex.CreateInactiveReference(setIndex, type, targetByteIndex);
        }

        internal unsafe void DeactivateTypeBatchPairs(int setIndex, Solver solver)
        {
            ref var constraintSet = ref solver.Sets[setIndex];
            ref var pairSet = ref InactiveSets[setIndex];

            for (int batchIndex = 0; batchIndex < constraintSet.Batches.Count; ++batchIndex)
            {
                ref var batch = ref constraintSet.Batches[batchIndex];
                for (int typeBatchIndex = 0; typeBatchIndex < batch.TypeBatches.Count; ++typeBatchIndex)
                {
                    ref var typeBatch = ref batch.TypeBatches[typeBatchIndex];
                    Debug.Assert(typeBatch.ConstraintCount > 0, "If a type batch exists, it should contain constraints.");
                    if (IsContactBatch(typeBatch.TypeId))
                    {
                        for (int indexInTypeBatch = 0; indexInTypeBatch < typeBatch.ConstraintCount; ++indexInTypeBatch)
                        {
                            var handle = typeBatch.IndexToHandle[indexInTypeBatch];
                            ref var pairLocation = ref ConstraintHandleToPair[handle];
                            //At the moment, we do *not* include the collision cache pointer in the handle mapping. The engine never needs it, and it cuts the size of the 
                            //mapping buffer from 24 bytes per entry to 16. We leave this here as an example of what would be needed should looking up a collision cache
                            //from constraint handle become useful. You would also need to update the PairCache.UpdateForExistingConstraint and PairCache.CompleteConstraintAdd.
                            //if (pairLocation.CollisionCache.Exists)
                            //    CopyToInactiveCache(setIndex, ref workerCaches[pairLocation.CollisionCache.Cache].collisionCaches, ref pairSet.collisionCaches, ref pairLocation.CollisionCache);
                            if (pairLocation.ConstraintCache.Exists)
                                CopyToInactiveCache(setIndex, ref workerCaches[pairLocation.ConstraintCache.Cache].constraintCaches, ref pairSet.constraintCaches, ref pairLocation.ConstraintCache);
                            //Now that any existing cache data has been moved into the inactive set, we should remove the overlap from the overlap mapping.
                            Mapping.FastRemove(ref pairLocation.Pair);
                        }
                    }
                }
            }
        }

        internal ref WorkerPairCache GetCacheForActivation()
        {
            //Note that the target location for the set depends on whether the activation is being executed from within the context of the narrow phase.
            //Either way, we need to put the data into the most recently updated cache. If this is happening inside the narrow phase, that is the NextWorkerCaches,
            //because we haven't yet flipped the buffers. If it's outside of the narrow phase, then it's the current workerCaches. 
            //We can distinguish between the two by checking whether the NextWorkerCaches are allocated. They don't exist outside of the narrowphase's execution.

            //Also note that we only deal with one worker cache. Activation just dumps new collision caches into the first thread. This works out since
            //the actual pair cache modification is locally sequential right now.
            if (NextWorkerCaches[0].collisionCaches.Allocated)
                return ref NextWorkerCaches[0];
            return ref workerCaches[0];
        }

        unsafe void CopyCachesForActivation(ref Buffer<UntypedList> source, ref Buffer<UntypedList> target)
        {
            for (int i = 0; i < source.Length; ++i)
            {
                ref var sourceCache = ref source[i];
                if (sourceCache.Buffer.Allocated)
                {
                    ref var targetCache = ref target[i];
                    Debug.Assert(targetCache.Buffer.Length >= targetCache.ByteCount + sourceCache.ByteCount,
                        "The capacity of relevant active set caches should have already been ensured.");
                    Debug.Assert(sourceCache.ElementSizeInBytes == targetCache.ElementSizeInBytes);
                    Unsafe.CopyBlockUnaligned(targetCache.Buffer.Memory + targetCache.ByteCount, sourceCache.Buffer.Memory, (uint)sourceCache.ByteCount);
                    targetCache.ByteCount += sourceCache.ByteCount;
                    targetCache.Count += sourceCache.ByteCount;
                }
            }
        }

        internal void ActivateSet(int setIndex)
        {
            ref var inactiveSet = ref InactiveSets[setIndex];
            Debug.Assert(inactiveSet.Allocated);
            ref var activeSet = ref GetCacheForActivation();
            for (int i = 0; i < inactiveSet.constraintCaches.Length; ++i)
            {
                ref var cache = ref inactiveSet.constraintCaches[i];
                if (cache.Buffer.Allocated)
                {
                    ref var activeCache = ref activeSet.constraintCaches[i];
                    //Debug.Assert(activeSet.collisionCaches[i])
                }
            }
            for (int i = 0; i < inactiveSet.collisionCaches.Length; ++i)
            {

            }
        }

        internal unsafe void GatherOldImpulses(ref ConstraintReference constraintReference, float* oldImpulses)
        {
            //Constraints cover 16 possible cases:
            //1-4 contacts: 0x3
            //convex vs nonconvex: 0x4
            //1 body versus 2 body: 0x8
            //TODO: Very likely that we'll expand the nonconvex manifold maximum to 8 contacts, so this will need to be adjusted later.

            //TODO: Note that we do not modify the friction accumulated impulses. This is just for simplicity- the impact of accumulated impulses on friction *should* be relatively
            //hard to notice compared to penetration impulses. We should, however, test this assumption.
            BundleIndexing.GetBundleIndices(constraintReference.IndexInTypeBatch, out var bundleIndex, out var inner);
            switch (constraintReference.TypeBatch.TypeId)
            {
                //1 body
                //Convex
                case 0:
                    {
                        //1 contact
                        ref var bundle = ref Buffer<Contact1AccumulatedImpulses>.Get(ref constraintReference.TypeBatch.AccumulatedImpulses, bundleIndex);
                        GatherScatter.GetLane(ref bundle.Penetration0, inner, ref *oldImpulses, 1);
                    }
                    break;
                case 1:
                    {
                        //2 contacts
                    }
                    break;
                case 2:
                    {
                        //3 contacts
                    }
                    break;
                case 3:
                    {
                        //4 contacts
                    }
                    break;
                //Nonconvex
                case 4 + 0:
                    {
                        //1 contact
                    }
                    break;

                case 4 + 1:
                    {
                        //2 contacts
                    }
                    break;
                case 4 + 2:
                    {
                        //3 contacts
                    }
                    break;
                case 4 + 3:
                    {
                        //4 contacts
                    }
                    break;
                //2 body
                //Convex
                case 8 + 0:
                    {
                        //1 contact
                        ref var bundle = ref Buffer<Contact1AccumulatedImpulses>.Get(ref constraintReference.TypeBatch.AccumulatedImpulses, bundleIndex);
                        GatherScatter.GetLane(ref bundle.Penetration0, inner, ref *oldImpulses, 1);
                    }
                    break;
                case 8 + 1:
                    {
                        //2 contacts
                    }
                    break;
                case 8 + 2:
                    {
                        //3 contacts
                    }
                    break;
                case 8 + 3:
                    {
                        //4 contacts
                        ref var bundle = ref Buffer<Contact4AccumulatedImpulses>.Get(ref constraintReference.TypeBatch.AccumulatedImpulses, bundleIndex);
                        GatherScatter.GetLane(ref bundle.Penetration0, inner, ref *oldImpulses, 4);
                    }
                    break;
                //Nonconvex
                case 8 + 4 + 0:
                    {
                        //1 contact
                    }
                    break;
                case 8 + 4 + 1:
                    {
                        //2 contacts
                    }
                    break;
                case 8 + 4 + 2:
                    {
                        //3 contacts
                    }
                    break;
                case 8 + 4 + 3:
                    {
                        //4 contacts
                    }
                    break;
            }
        }

        internal void ScatterNewImpulses<TContactImpulses>(ref ConstraintReference constraintReference, ref TContactImpulses contactImpulses)
        {
            //Constraints cover 16 possible cases:
            //1-4 contacts: 0x3
            //convex vs nonconvex: 0x4
            //1 body versus 2 body: 0x8
            //TODO: Very likely that we'll expand the nonconvex manifold maximum to 8 contacts, so this will need to be adjusted later.

            //TODO: Note that we do not modify the friction accumulated impulses. This is just for simplicity- the impact of accumulated impulses on friction *should* be relatively
            //hard to notice compared to penetration impulses. We should, however, test this assumption.
            BundleIndexing.GetBundleIndices(constraintReference.IndexInTypeBatch, out var bundleIndex, out var inner);
            switch (constraintReference.TypeBatch.TypeId)
            {
                //1 body
                //Convex
                case 0:
                    {
                        //1 contact
                        ref var bundle = ref Buffer<Contact1AccumulatedImpulses>.Get(ref constraintReference.TypeBatch.AccumulatedImpulses, bundleIndex);
                        GatherScatter.SetLane(ref bundle.Penetration0, inner, ref Unsafe.As<TContactImpulses, float>(ref contactImpulses), 1);
                    }
                    break;
                case 1:
                    {
                        //2 contacts
                    }
                    break;
                case 2:
                    {
                        //3 contacts
                    }
                    break;
                case 3:
                    {
                        //4 contacts
                    }
                    break;
                //Nonconvex
                case 4 + 0:
                    {
                        //1 contact
                    }
                    break;
                case 4 + 1:
                    {
                        //2 contacts
                    }
                    break;
                case 4 + 2:
                    {
                        //3 contacts
                    }
                    break;
                case 4 + 3:
                    {
                        //4 contacts
                    }
                    break;
                //2 body
                //Convex
                case 8 + 0:
                    {
                        //1 contact
                        ref var bundle = ref Buffer<Contact1AccumulatedImpulses>.Get(ref constraintReference.TypeBatch.AccumulatedImpulses, bundleIndex);
                        GatherScatter.SetLane(ref bundle.Penetration0, inner, ref Unsafe.As<TContactImpulses, float>(ref contactImpulses), 1);
                    }
                    break;
                case 8 + 1:
                    {
                        //2 contacts
                    }
                    break;
                case 8 + 2:
                    {
                        //3 contacts
                    }
                    break;
                case 8 + 3:
                    {
                        //4 contacts
                        ref var bundle = ref Buffer<Contact4AccumulatedImpulses>.Get(ref constraintReference.TypeBatch.AccumulatedImpulses, bundleIndex);
                        GatherScatter.SetLane(ref bundle.Penetration0, inner, ref Unsafe.As<TContactImpulses, float>(ref contactImpulses), 4);
                    }
                    break;
                //Nonconvex
                case 8 + 4 + 0:
                    {
                        //1 contact
                    }
                    break;
                case 8 + 4 + 1:
                    {
                        //2 contacts
                    }
                    break;
                case 8 + 4 + 2:
                    {
                        //3 contacts
                    }
                    break;
                case 8 + 4 + 3:
                    {
                        //4 contacts
                    }
                    break;
            }

        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal unsafe void* GetOldConstraintCachePointer(int pairIndex)
        {
            ref var constraintCacheIndex = ref Mapping.Values[pairIndex].ConstraintCache;
            return workerCaches[constraintCacheIndex.Cache].GetConstraintCachePointer(constraintCacheIndex);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal unsafe int GetOldConstraintHandle(int pairIndex)
        {
            ref var constraintCacheIndex = ref Mapping.Values[pairIndex].ConstraintCache;
            return *(int*)workerCaches[constraintCacheIndex.Cache].GetConstraintCachePointer(constraintCacheIndex);
        }

        /// <summary>
        /// Completes the addition of a constraint by filling in the narrowphase's pointer to the constraint and by distributing accumulated impulses.
        /// </summary>
        /// <typeparam name="TContactImpulses">Count-specialized type containing cached accumulated impulses.</typeparam>
        /// <param name="solver">Solver containing the constraint to set the impulses of.</param>
        /// <param name="impulses">Warm starting impulses to apply to the contact constraint.</param>
        /// <param name="constraintCacheIndex">Index of the constraint cache to update.</param>
        /// <param name="constraintHandle">Constraint handle associated with the constraint cache being updated.</param>
        /// <param name="pair">Collidable pair associated with the new constraint.</param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal unsafe void CompleteConstraintAdd<TContactImpulses>(Solver solver, ref TContactImpulses impulses, PairCacheIndex constraintCacheIndex,
            int constraintHandle, ref CollidablePair pair)
        {
            //Note that the update is being directed to the *next* worker caches. We have not yet performed the flush that swaps references.
            //Note that this assumes that the constraint handle is stored in the first 4 bytes of the constraint cache.
            *(int*)NextWorkerCaches[constraintCacheIndex.Cache].GetConstraintCachePointer(constraintCacheIndex) = constraintHandle;
            solver.GetConstraintReference(constraintHandle, out var reference);
            ScatterNewImpulses(ref reference, ref impulses);
            //This mapping entry had to be deferred until now because no constraint handle was known until now. Now that we have it,
            //we can fill in the pointers back to the overlap mapping and constraint cache.
            ref var pairReference = ref ConstraintHandleToPair[constraintHandle];
            pairReference.Pair = pair;
            pairReference.ConstraintCache = constraintCacheIndex;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public unsafe ref TConstraintCache GetConstraintCache<TConstraintCache>(PairCacheIndex constraintCacheIndex)
        {
            //Note that these refer to the previous workerCaches, not the nextWorkerCaches. We read from these caches during the narrowphase to redistribute impulses.
            return ref Unsafe.AsRef<TConstraintCache>(workerCaches[constraintCacheIndex.Cache].GetConstraintCachePointer(constraintCacheIndex));
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public unsafe ref TCollisionData GetCollisionData<TCollisionData>(PairCacheIndex index) where TCollisionData : struct, IPairCacheEntry
        {
            return ref Unsafe.AsRef<TCollisionData>(workerCaches[index.Cache].GetCollisionCachePointer(index));
        }

    }
}
