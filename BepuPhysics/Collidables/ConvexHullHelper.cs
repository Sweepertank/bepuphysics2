﻿using BepuUtilities;
using BepuUtilities.Collections;
using BepuUtilities.Memory;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Text;

namespace BepuPhysics.Collidables
{
    /// <summary>
    /// Stores references to the points composing one of a convex hull's faces.
    /// </summary>
    public struct HullFace
    {
        public Buffer<Vector3> Vertices;
        public Buffer<int> VertexIndices;

        /// <summary>
        /// Gets the number of vertices in the face.
        /// </summary>
        public int VertexCount
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get { return VertexIndices.Length; }
        }

        /// <summary>
        /// Gets a reference to the vertex position associated with the given face vertex index.
        /// </summary>
        /// <param name="index">Index into the face's vertex list.</param>
        /// <returns>Reference to the vertex position associated with the given face vertex index.</returns>
        public ref Vector3 this[int index]
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get { return ref Vertices[VertexIndices[index]]; }
        }
    }

    /// <summary>
    /// Raw data representing a convex hull.
    /// </summary>
    /// <remarks>This is not yet transformed into a runtime format. It requires additional processing to be used in a ConvexHull shape; see ConvexHullHelper.ProcessHull.</remarks>
    public struct HullData
    {
        /// <summary>
        /// Points on the surface of the convex hull.
        /// </summary>
        public Buffer<Vector3> Vertices;
        /// <summary>
        /// List of indices composing the faces of the hull. Individual faces indexed by the FaceIndices.
        /// </summary>
        public Buffer<int> FaceVertexIndices;
        /// <summary>
        /// Starting index in the FaceVertexIndices for each face.
        /// </summary>
        public Buffer<int> FaceStartIndices;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void GetFace(int faceIndex, out HullFace face)
        {
            var nextFaceIndex = faceIndex + 1;
            var start = FaceStartIndices[faceIndex];
            var end = nextFaceIndex == FaceStartIndices.Length ? FaceStartIndices.Length : FaceStartIndices[nextFaceIndex];
            FaceVertexIndices.Slice(start, end - start, out face.VertexIndices);
            face.Vertices = Vertices;
        }
    }

    /// <summary>
    /// Helper methods to create and process convex hulls from point clouds.
    /// </summary>
    public static class ConvexHullHelper
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static void InitializeIndices(out Vector<int> indices)
        {
            ref var start = ref Unsafe.As<Vector<int>, int>(ref indices);
            start = 0;
            for (int i = 1; i < Vector<int>.Count; ++i)
            {
                Unsafe.Add(ref start, i) = i;
            }
        }

        static void FindExtremeVertices(in Vector3Wide basisX, in Vector3Wide basisY, in Vector3Wide basisOrigin, ref Buffer<Vector3Wide> points, in Vector<int> indexOffsets,
            ref Buffer<Vector<float>> projectedOnX, ref Buffer<Vector<float>> projectedOnY, in Vector<float> planeEpsilon, ref QuickList<int> vertexIndices, out Vector3 faceNormal)
        {
            Debug.Assert(projectedOnX.Length >= points.Length && projectedOnY.Length >= points.Length && vertexIndices.Count == 0 && vertexIndices.Span.Length >= points.Length * Vector<float>.Count);
            //Find the candidate-basisOrigin which has the greatest angle with basisX when projected onto the plane spanned by basisX and basisY.
            //(Candidates which are on the wrong side of the basisX axis- that is, they have a negative dot product with basisY- are ignored.)
            //angle = acos(x / ||(x, y)||)
            //cosAngle = x / ||(x, y)||
            //cosAngle^2 = x^2 / ||(x, y)||^2
            //We can then compare samples 0 and 1 using:
            //sign(x0) * x0^2 * ||(x1,y1)||^2 < sign(x1) * x1^2 * ||(x0,y0)||^2
            //with no divisions, square roots, or trigonometry.
            Vector3Wide.Subtract(points[0], basisOrigin, out var toCandidate);
            ref var x = ref projectedOnX[0];
            ref var y = ref projectedOnY[0];
            Vector3Wide.Dot(basisX, toCandidate, out x);
            Vector3Wide.Dot(basisY, toCandidate, out y);
            var bestNumerators = x * x;
            var bestDenominators = bestNumerators + y * y;
            bestNumerators = Vector.ConditionalSelect(Vector.LessThan(x, Vector<float>.Zero), -bestNumerators, bestNumerators);
            //In addition to searching 'away' from the source face, make sure that the source edge doesn't get picked by numerical oddity. (Consider index based.)
            var epsilon = new Vector<float>(1e-7f);
            var ignoreSlot = Vector.LessThan(y, epsilon);
            bestDenominators = Vector.ConditionalSelect(ignoreSlot, Vector<float>.One, bestDenominators);
            bestNumerators = Vector.ConditionalSelect(ignoreSlot, Vector<float>.One, bestNumerators);
            var bestIndices = indexOffsets;
            for (int i = 1; i < points.Length; ++i)
            {
                Vector3Wide.Subtract(points[i], basisOrigin, out toCandidate);
                x = ref projectedOnX[i];
                y = ref projectedOnY[i];
                Vector3Wide.Dot(basisX, toCandidate, out x);
                Vector3Wide.Dot(basisY, toCandidate, out y);
                var candidateNumerator = x * x;
                var candidateDenominator = bestNumerators + y * y;
                candidateNumerator = Vector.ConditionalSelect(Vector.LessThan(x, Vector<float>.Zero), -candidateNumerator, candidateNumerator);

                var useCandidate = Vector.BitwiseAnd(Vector.GreaterThan(y, epsilon), Vector.LessThan(candidateNumerator * bestDenominators, bestNumerators * candidateDenominator));
                var candidateIndices = indexOffsets + new Vector<int>(i << BundleIndexing.VectorShift);
                bestIndices = Vector.ConditionalSelect(useCandidate, candidateIndices, bestIndices);
                bestNumerators = Vector.ConditionalSelect(useCandidate, candidateNumerator, bestNumerators);
                bestDenominators = Vector.ConditionalSelect(useCandidate, candidateDenominator, bestDenominators);
            }
            var bestNumerator = bestNumerators[0];
            var bestDenominator = bestDenominators[0];
            var bestIndex = bestIndices[0];
            for (int i = 1; i < Vector<float>.Count; ++i)
            {
                var candidateNumerator = bestNumerators[i];
                var candidateDenominator = bestDenominators[i];
                if (candidateNumerator * bestDenominator < bestNumerator * candidateDenominator)
                {
                    bestNumerator = candidateNumerator;
                    bestDenominator = candidateDenominator;
                    bestIndex = bestIndices[i];
                }
            }
            //We now have the best index, but there may have been multiple vertices on the same plane. Capture all of them at once by doing a second pass over the results.
            //The plane normal we want to examine is (-bestY, bestX) / ||(-bestY, bestX)||.
            //(This isn't wonderfully fast, but it's fairly simple. The alternatives are things like incrementally combining coplanar triangles as they are discovered
            //or using a postpass that looks for coplanar triangles after they've been created.)
            BundleIndexing.GetBundleIndices(bestIndex, out var bestBundleIndex, out int bestInnerIndex);
            var bestX = projectedOnX[bestBundleIndex][bestInnerIndex];
            var bestY = projectedOnY[bestBundleIndex][bestInnerIndex];
            var projectedPlaneNormalNarrow = Vector2.Normalize(new Vector2(-bestY, bestX));
            Vector2Wide.Broadcast(projectedPlaneNormalNarrow, out var projectedPlaneNormal);
            for (int i = 0; i < points.Length; ++i)
            {
                var dot = projectedOnX[i] * projectedPlaneNormal.X + projectedOnY[i] * projectedPlaneNormal.Y;
                var coplanar = Vector.LessThanOrEqual(Vector.Abs(dot), planeEpsilon);
                if (Vector.LessThanAny(coplanar, Vector<int>.Zero))
                {
                    for (int j = 0; j < Vector<int>.Count; ++j)
                    {
                        if (coplanar[j] < 0)
                        {
                            vertexIndices.AllocateUnsafely() = (i << BundleIndexing.VectorShift) + j;
                        }
                    }
                }
            }
            Vector3Wide.ReadFirst(basisX, out var basisXNarrow);
            Vector3Wide.ReadFirst(basisY, out var basisYNarrow);
            faceNormal = basisXNarrow * projectedPlaneNormalNarrow.X + basisYNarrow * projectedPlaneNormalNarrow.Y;
        }


        static int FindNextIndexForFaceHull(in Vector2 start, int startIndex, in Vector2 previousEdgeDirection, ref QuickList<Vector2> facePoints)
        {
            //Use a AOS version since the number of points on a given face will tend to be very small in most cases.
            //Same idea as the 3d version- find the next edge which is closest to the previous edge. Not going to worry about collinear points here for now.
            var startToCandidate = facePoints[0] - start;
            var dot = Vector2.Dot(startToCandidate, previousEdgeDirection);
            var bestNumerator = dot * dot;
            bestNumerator = dot < 0 ? -bestNumerator : bestNumerator;
            var bestDenominator = startToCandidate.LengthSquared();
            var bestIndex = 0;
            if (startIndex == 0)
            {
                bestNumerator = -1;
                bestDenominator = 1;
            }
            for (int i = 0; i < facePoints.Count; ++i)
            {
                startToCandidate = facePoints[0] - start;
                dot = Vector2.Dot(startToCandidate, previousEdgeDirection);
                var candidateNumerator = dot * dot;
                candidateNumerator = dot < 0 ? -candidateNumerator : candidateNumerator;
                var candidateDenominator = startToCandidate.LengthSquared();
                if (candidateNumerator * bestDenominator > bestNumerator * candidateDenominator)
                {
                    bestNumerator = candidateNumerator;
                    bestDenominator = candidateDenominator;
                    bestIndex = i;
                }
            }
            return bestIndex;
        }
        static void ReduceFace(ref QuickList<int> faceVertexIndices, in Vector3 faceNormal, ref Buffer<Vector3> points, ref QuickList<Vector2> facePoints, ref QuickList<int> reducedIndices)
        {
            Debug.Assert(facePoints.Count == 0 && reducedIndices.Count == 0 && facePoints.Span.Length >= faceVertexIndices.Count && reducedIndices.Span.Length >= faceVertexIndices.Count);
            if (faceVertexIndices.Count <= 3)
            {
                //Too small to require computing a hull. Copy directly.
                for (int i = 0; i < faceVertexIndices.Count; ++i)
                {
                    reducedIndices.AllocateUnsafely() = faceVertexIndices[i];
                }
                return;
            }
            Helpers.BuildOrthnormalBasis(faceNormal, out var basisX, out var basisY);
            Vector2 centroid = default;
            for (int i = 0; i < faceVertexIndices.Count; ++i)
            {
                ref var source = ref points[faceVertexIndices[i]];
                ref var facePoint = ref facePoints.AllocateUnsafely();
                facePoint = new Vector2(Vector3.Dot(basisX, source), Vector3.Dot(basisY, source));
                centroid += facePoint;
            }
            centroid /= faceVertexIndices.Count;
            var greatestDistanceSquared = -1f;
            var initialIndex = 0;
            for (int i = 0; i < faceVertexIndices.Count; ++i)
            {
                ref var facePoint = ref facePoints[i];
                var distanceSquared = (facePoint - centroid).LengthSquared();
                if (greatestDistanceSquared < distanceSquared)
                {
                    greatestDistanceSquared = distanceSquared;
                    initialIndex = i;
                }
            }

            if (greatestDistanceSquared < 1e-14f)
            {
                //The face is degenerate.
                reducedIndices.AllocateUnsafely() = faceVertexIndices[0];
                return;
            }
            var initialOffsetDirection = (facePoints[initialIndex] - centroid) / (float)Math.Sqrt(greatestDistanceSquared);
            var previousEdgeDirection = new Vector2(-initialOffsetDirection.Y, initialOffsetDirection.X);
            reducedIndices.AllocateUnsafely() = initialIndex;

            var previousIndex = initialIndex;
            while (true)
            {
                var nextIndex = FindNextIndexForFaceHull(facePoints[previousIndex], previousIndex, previousEdgeDirection, ref facePoints);
                if (nextIndex == initialIndex)
                {
                    //Found our way back to the start; exit.
                    break;
                }
                reducedIndices.AllocateUnsafely() = nextIndex;
                previousEdgeDirection = Vector2.Normalize(facePoints[nextIndex] - facePoints[previousIndex]);
                previousIndex = nextIndex;
            }

        }

        /// <summary>
        /// Computes the convex hull of a set of points.
        /// </summary>
        /// <param name="points">Point set to compute the convex hull of.</param>
        /// <param name="pool">Buffer pool to pull memory from when creating the hull.</param>
        /// <param name="hullData">Convex hull of the input point set.</param>
        public static void ComputeHull(Buffer<Vector3> points, BufferPool pool, out HullData hullData)
        {
            if (points.Length <= 0)
            {
                hullData = default;
                return;
            }
            if (points.Length <= 3)
            {
                //If the input is too small to actually form a volumetric hull, just output the input directly.
                pool.Take(points.Length, out hullData.Vertices);
                points.CopyTo(0, ref hullData.Vertices, 0, points.Length);
                hullData.Vertices.Slice(0, points.Length, out hullData.Vertices);
                if (points.Length == 3)
                {
                    pool.Take(1, out hullData.FaceStartIndices);
                    pool.Take(3, out hullData.FaceVertexIndices);
                    hullData.FaceStartIndices[0] = 0;
                    hullData.FaceVertexIndices[0] = 0;
                    hullData.FaceVertexIndices[1] = 1;
                    hullData.FaceVertexIndices[2] = 2;
                    hullData.FaceStartIndices.Slice(0, 1, out hullData.FaceStartIndices);
                    hullData.FaceVertexIndices.Slice(0, 3, out hullData.FaceVertexIndices);
                }
                else
                {
                    hullData.FaceStartIndices = default;
                    hullData.FaceVertexIndices = default;
                }
                return;
            }
            pool.Take<Vector3Wide>(BundleIndexing.GetBundleCount(points.Length), out var pointBundles);
            pointBundles.Slice(0, points.Length >> BundleIndexing.VectorShift, out pointBundles);
            //While it's not asymptotically optimal in general, gift wrapping is simple and easy to productively vectorize.
            //As a first step, create an AOSOA version of the input data.
            Vector3 centroid = default;
            for (int i = 0; i < points.Length; ++i)
            {
                BundleIndexing.GetBundleIndices(i, out var bundleIndex, out var innerIndex);
                ref var p = ref points[i];
                Vector3Wide.WriteSlot(p, innerIndex, ref pointBundles[bundleIndex]);
                centroid += p;
            }
            centroid /= points.Length;
            //Fill in the last few slots with the centroid.
            //We avoid doing a bunch of special case work on the last partial bundle by just assuming it has a few extra redundant internal points. 
            var bundleSlots = pointBundles.Length * Vector<float>.Count;
            for (int i = points.Length; i < bundleSlots; ++i)
            {
                BundleIndexing.GetBundleIndices(i, out var bundleIndex, out var innerIndex);
                Vector3Wide.WriteSlot(centroid, innerIndex, ref pointBundles[bundleIndex]);
            }

            //Find a starting point. We'll use the one furthest from the centroid.
            Vector3Wide.Broadcast(centroid, out var centroidBundle);
            InitializeIndices(out var mostDistantIndicesBundle);
            var indexOffsetBundle = mostDistantIndicesBundle;
            Vector3Wide.DistanceSquared(pointBundles[0], centroidBundle, out var distanceSquaredBundle);
            for (int i = 1; i < pointBundles.Length; ++i)
            {
                var bundleIndices = new Vector<int>(i << BundleIndexing.VectorShift) + indexOffsetBundle;
                Vector3Wide.DistanceSquared(pointBundles[i], centroidBundle, out var distanceSquaredCandidate);
                mostDistantIndicesBundle = Vector.ConditionalSelect(Vector.LessThan(distanceSquaredCandidate, distanceSquaredBundle), bundleIndices, mostDistantIndicesBundle);
                distanceSquaredBundle = Vector.Min(distanceSquaredBundle, distanceSquaredCandidate);
            }
            var bestDistanceSquared = distanceSquaredBundle[0];
            var initialIndex = 0;
            for (int i = 1; i < Vector<int>.Count; ++i)
            {
                var distanceCandidate = distanceSquaredBundle[i];
                if (bestDistanceSquared > distanceCandidate)
                {
                    bestDistanceSquared = distanceCandidate;
                    initialIndex = mostDistantIndicesBundle[i];
                }
            }
            pool.Take<Vector3>(points.Length, out var vertices);
            BundleIndexing.GetBundleIndices(initialIndex, out var mostDistantBundleIndex, out var mostDistantInnerIndex);
            ref var initialVertex = ref vertices[0];
            Vector3Wide.ReadSlot(ref pointBundles[mostDistantBundleIndex], mostDistantInnerIndex, out initialVertex);

            //All further points will be found by picking an plane on which to project all vertices down onto, and then measuring the angle on that plane.
            //We pick to basis directions along which to measure. For the second point, we choose a perpendicular direction arbitrarily.
            var initialToCentroid = centroid - initialVertex;
            var initialDistance = initialToCentroid.Length();
            if (initialDistance < 1e-7f)
            {
                //The point set lacks any volume or area.
                pool.Take(1, out hullData.Vertices);
                hullData.Vertices[0] = points[0];
                hullData.Vertices.Slice(0, 1, out hullData.Vertices);
                hullData.FaceStartIndices = default;
                hullData.FaceVertexIndices = default;
            }
            Vector3Wide.Broadcast(initialVertex / initialDistance, out var initialBasisX);
            Helpers.FindPerpendicular(initialBasisX, out var initialBasisY); //(broadcasted before FindPerpendicular just because we didn't have a non-bundle version)
            Vector3Wide.Broadcast(initialVertex, out var initialVertexBundle);
            pool.Take<Vector<float>>(pointBundles.Length, out var projectedOnX);
            pool.Take<Vector<float>>(pointBundles.Length, out var projectedOnY);
            var planeEpsilon = new Vector<float>((float)Math.Sqrt(bestDistanceSquared) * 1e-7f);
            var rawFaceVertexIndices = new QuickList<int>(points.Length, pool);
            FindExtremeVertices(initialBasisX, initialBasisY, initialVertexBundle, ref pointBundles, indexOffsetBundle,
               ref projectedOnX, ref projectedOnY, planeEpsilon, ref rawFaceVertexIndices, out var faceNormal);
            Debug.Assert(rawFaceVertexIndices.Count >= 2);
            var facePoints = new QuickList<Vector2>(points.Length, pool);
            var reducedFaceIndices = new QuickList<int>(points.Length, pool);
            ReduceFace(ref rawFaceVertexIndices, faceNormal, ref points, ref facePoints, ref reducedFaceIndices);

            var edgesToTest = new QuickList<Int2>(points.Length, pool);
            for (int i = 1; i < reducedFaceIndices.Count; ++i)
            {
                ref var edgeToAdd = ref edgesToTest.Allocate(pool);
                edgeToAdd.X = reducedFaceIndices[i - 1];
                edgeToAdd.Y = reducedFaceIndices[i];
            }
            var edgeFaceCounts = new QuickDictionary<Int2, int, Int2>(points.Length, pool);

            while (edgesToTest.Count > 0)
            {
                edgesToTest.Pop(out var edgeToTest);
                //Make sure the new edge hasn't already been filled by another traversal.
                var faceCountIndex = edgeFaceCounts.IndexOf(edgeToTest);
                if (faceCountIndex >= 0 && edgeFaceCounts.Values[faceCountIndex] == 2)
                    continue;

            }



            hullData = default;
        }



        /// <summary>
        /// Processes raw hull data into a runtime usable convex hull shape.
        /// </summary>
        /// <param name="hullData">Raw input data to process.</param>
        /// <param name="hullShape">Convex hull shape created from the input data.</param>
        public static void ProcessHull(HullData hullData, out ConvexHull hullShape)
        {
            hullShape = default;
        }
    }
}