﻿using BepuPhysics.Collidables;
using BepuPhysics.CollisionDetection.SweepTasks;
using BepuUtilities;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Text;

namespace BepuPhysics.CollisionDetection
{

    public struct SimplexTilterVertex
    {
        public Vector3 Support;
        public Vector3 Normal;
        public float Depth;
        public bool Exists;
    }

    public enum SimplexTilterNormalSource
    {
        Initialization = -1,
        TriangleFace = 0,
        Edge = 1,
        Vertex = 2,
        //EdgeReflect = 2,
        //Contraction = 3,
    }

    public struct SimplexTilterStep
    {
        public SimplexTilterVertex A;
        public SimplexTilterVertex B;
        public SimplexTilterVertex C;
        public SimplexTilterVertex D;
        public float ProgressionScale;
        public SimplexTilterNormalSource NextNormalSource;
        public Vector3 ClosestPointOnTriangle;
        public Vector3 TiltStart;
        public Vector3 TiltTargetPoint;
        public Vector3 TiltOffset;
        public Vector3 NextNormal;

        public float BestDepth;
        public Vector3 BestNormal;
    }


    public static class SimplexTilter<TShapeA, TShapeWideA, TSupportFinderA, TShapeB, TShapeWideB, TSupportFinderB>
        where TShapeA : IConvexShape
        where TShapeWideA : IShapeWide<TShapeA>
        where TSupportFinderA : ISupportFinder<TShapeA, TShapeWideA>
        where TShapeB : IConvexShape
        where TShapeWideB : IShapeWide<TShapeB>
        where TSupportFinderB : ISupportFinder<TShapeB, TShapeWideB>
    {
        public struct Vertex
        {
            public Vector3Wide Support;
            public Vector3Wide Normal;
            public Vector<float> Depth;
            public Vector<int> Exists;
        }

        public struct Simplex
        {
            public Vertex A;
            public Vertex B;
            public Vertex C;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static void FillSlot(ref Vertex vertex, in Vector3Wide support, in Vector3Wide normal, in Vector<float> depth)
        {
            //Note that this always fills empty slots. That's important- we avoid figuring out what subsimplex is active
            //and instead just treat it as a degenerate simplex with some duplicates. (Shares code with the actual degenerate path.)
            Vector3Wide.ConditionalSelect(vertex.Exists, vertex.Support, support, out vertex.Support);
            Vector3Wide.ConditionalSelect(vertex.Exists, vertex.Normal, normal, out vertex.Normal);
            vertex.Depth = Vector.ConditionalSelect(vertex.Exists, vertex.Depth, depth);
            vertex.Exists = new Vector<int>(-1);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static void ForceFillSlot(in Vector<int> shouldFill, ref Vertex vertex, in Vector3Wide support, in Vector3Wide normal, in Vector<float> depth)
        {
            vertex.Exists = Vector.BitwiseOr(vertex.Exists, shouldFill);
            Vector3Wide.ConditionalSelect(shouldFill, support, vertex.Support, out vertex.Support);
            Vector3Wide.ConditionalSelect(shouldFill, normal, vertex.Normal, out vertex.Normal);
            vertex.Depth = Vector.ConditionalSelect(vertex.Exists, vertex.Depth, depth);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static void Swap(in Vector<int> shouldSwap, ref Vertex a, ref Vertex b)
        {
            var temp = a;
            Vector3Wide.ConditionalSelect(shouldSwap, b.Support, a.Support, out a.Support);
            Vector3Wide.ConditionalSelect(shouldSwap, b.Normal, a.Normal, out a.Normal);
            a.Depth = Vector.ConditionalSelect(shouldSwap, b.Depth, a.Depth);
            Vector3Wide.ConditionalSelect(shouldSwap, temp.Support, b.Support, out b.Support);
            Vector3Wide.ConditionalSelect(shouldSwap, temp.Normal, b.Normal, out b.Normal);
            b.Depth = Vector.ConditionalSelect(shouldSwap, temp.Depth, b.Depth);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Create(in Vector3Wide normal, in Vector3Wide support, in Vector<float> depth, out Simplex simplex)
        {
            //While only one slot is actually full, GetNextNormal expects every slot to have some kind of data-
            //for those slots which are not yet filled, it should be duplicates of other data.
            //(The sub-triangle case is treated the same as the degenerate case.)
            simplex.A.Support = support;
            simplex.B.Support = support;
            simplex.C.Support = support;
            simplex.A.Normal = normal;
            simplex.B.Normal = normal;
            simplex.C.Normal = normal;
            simplex.A.Depth = depth;
            simplex.B.Depth = depth;
            simplex.C.Depth = depth;
            simplex.A.Exists = new Vector<int>(-1);
            simplex.B.Exists = Vector<int>.Zero;
            simplex.C.Exists = Vector<int>.Zero;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void FindSupport(in TShapeWideA a, in TShapeWideB b, in Vector3Wide localOffsetB, in Matrix3x3Wide localOrientationB, ref TSupportFinderA supportFinderA, ref TSupportFinderB supportFinderB, in Vector3Wide direction, out Vector3Wide support)
        {
            //support(N, A) - support(-N, B)
            supportFinderA.ComputeLocalSupport(a, direction, out var extremeA);
            Vector3Wide.Negate(direction, out var negatedDirection);
            supportFinderB.ComputeSupport(b, localOrientationB, negatedDirection, out var extremeB);
            Vector3Wide.Add(extremeB, localOffsetB, out extremeB);

            Vector3Wide.Subtract(extremeA, extremeB, out support);
        }

        struct HasNewSupport { }
        struct HasNoNewSupport { }
        static void GetNextNormal<T>(ref Simplex simplex, in Vector3Wide localOffsetB, in Vector3Wide support, in Vector3Wide normal, in Vector<float> depth, ref Vector<float> progressionScale, in Vector<int> terminatedLanes,
            in Vector3Wide bestNormal, in Vector<float> bestDepth,
            out Vector3Wide nextNormal, out SimplexTilterStep step)
        {
            //Try to add the new support to the simplex. A couple of cases:
            //1) The simplex is not yet a triangle, or the triangle does not yet contain the projected origin.
            //Choose the next sample location by detecting which simplex feature is closest to the origin and tilting in that direction.
            //Simplex entries unrelated to the nearest feature are discarded.
            //2) The simplex is a triangle and contains the projected origin. Choose the triangle's normal as the next sample direction.

            //If the simplex is already a triangle and there's a new support, that means the previous iteration created a sample direction from 
            //a triangle normal (no vertices were discarded). There are now three subtriangles- ABD, BCD, and CAD.
            //Choose one and let it fall through to the normal case.

            {
                //DEBUG STUFF
                step = default;
                Vector3Wide.ReadFirst(simplex.A.Support, out step.A.Support);
                Vector3Wide.ReadFirst(simplex.B.Support, out step.B.Support);
                Vector3Wide.ReadFirst(simplex.C.Support, out step.C.Support);
                Vector3Wide.ReadFirst(support, out step.D.Support);
                Vector3Wide.ReadFirst(simplex.A.Normal, out step.A.Normal);
                Vector3Wide.ReadFirst(simplex.B.Normal, out step.B.Normal);
                Vector3Wide.ReadFirst(simplex.C.Normal, out step.C.Normal);
                Vector3Wide.ReadFirst(normal, out step.D.Normal);
                step.A.Depth = simplex.A.Depth[0];
                step.B.Depth = simplex.B.Depth[0];
                step.C.Depth = simplex.C.Depth[0];
                step.D.Depth = depth[0];
                step.A.Exists = simplex.A.Exists[0] < 0;
                step.B.Exists = simplex.B.Exists[0] < 0;
                step.C.Exists = simplex.C.Exists[0] < 0;
                step.D.Exists = typeof(T) == typeof(HasNewSupport);
            }

            if (typeof(T) == typeof(HasNewSupport))
            {
                var simplexFull = Vector.BitwiseAnd(simplex.A.Exists, Vector.BitwiseAnd(simplex.B.Exists, simplex.C.Exists));
                //Fill any empty slots with the new support. Combines partial simplex case with degenerate simplex case.
                FillSlot(ref simplex.A, support, normal, depth);
                FillSlot(ref simplex.B, support, normal, depth);
                FillSlot(ref simplex.C, support, normal, depth);



                var activeFullSimplex = Vector.AndNot(simplexFull, terminatedLanes);
                if (Vector.LessThanAny(activeFullSimplex, Vector<int>.Zero))
                {
                    //At least one active lane has a full simplex and an incoming new sample.
                    //Choose one of the resulting triangles based on which triangle contains the current best normal.
                    //The three edges we need to test are: ADO, BDO, CDO.
                    Vector3Wide.CrossWithoutOverlap(simplex.A.Support, support, out var axd);
                    Vector3Wide.CrossWithoutOverlap(simplex.B.Support, support, out var bxd);
                    Vector3Wide.CrossWithoutOverlap(simplex.C.Support, support, out var cxd);
                    Vector3Wide.Dot(axd, bestNormal, out var adPlaneTest);
                    Vector3Wide.Dot(bxd, bestNormal, out var bdPlaneTest);
                    Vector3Wide.Dot(cxd, bestNormal, out var cdPlaneTest);

                    var useABD = Vector.BitwiseAnd(Vector.GreaterThanOrEqual(adPlaneTest, Vector<float>.Zero), Vector.LessThan(bdPlaneTest, Vector<float>.Zero));
                    var useBCD = Vector.BitwiseAnd(Vector.GreaterThanOrEqual(bdPlaneTest, Vector<float>.Zero), Vector.LessThan(cdPlaneTest, Vector<float>.Zero));
                    var useCAD = Vector.BitwiseAnd(Vector.GreaterThanOrEqual(cdPlaneTest, Vector<float>.Zero), Vector.LessThan(adPlaneTest, Vector<float>.Zero));

                    //Because the best normal may have changed due to the latest sample, ABC's portal may not contain the best normal anymore, which means none of the
                    //subtriangles do either. This is fairly rare and the fallback heuristic doesn't matter much- this won't cause cycles because
                    //it can only occur on iterations where depth improvement has been made (and thus the best normal has changed).
                    //So we'll do something stupid!
                    useABD = Vector.ConditionalSelect(Vector.OnesComplement(Vector.BitwiseOr(Vector.BitwiseOr(useABD, useBCD), useCAD)), new Vector<int>(-1), useABD);

                    ForceFillSlot(Vector.BitwiseAnd(useBCD, simplexFull), ref simplex.A, support, normal, depth);
                    ForceFillSlot(Vector.BitwiseAnd(useCAD, simplexFull), ref simplex.B, support, normal, depth);
                    ForceFillSlot(Vector.BitwiseAnd(useABD, simplexFull), ref simplex.C, support, normal, depth);

                }
            }
            else
            {
                FillSlot(ref simplex.A, simplex.A.Support, simplex.A.Normal, simplex.A.Depth);
                FillSlot(ref simplex.B, simplex.A.Support, simplex.A.Normal, simplex.A.Depth);
                FillSlot(ref simplex.C, simplex.A.Support, simplex.A.Normal, simplex.A.Depth);
            }
            Vector3Wide.Subtract(simplex.B.Support, simplex.A.Support, out var ab);
            Vector3Wide.Subtract(simplex.A.Support, simplex.C.Support, out var ca);
            Vector3Wide.Subtract(simplex.C.Support, simplex.B.Support, out var bc);
            Vector3Wide.CrossWithoutOverlap(ab, ca, out var triangleNormal);
            Vector3Wide.LengthSquared(triangleNormal, out var triangleNormalLengthSquared);

            Vector3Wide.LengthSquared(ab, out var abLengthSquared);
            Vector3Wide.LengthSquared(bc, out var bcLengthSquared);
            Vector3Wide.LengthSquared(ca, out var caLengthSquared);
            var longestEdgeLengthSquared = Vector.Max(Vector.Max(abLengthSquared, bcLengthSquared), caLengthSquared);
            var simplexDegenerate = Vector.LessThanOrEqual(triangleNormalLengthSquared, longestEdgeLengthSquared * 1e-10f);
            var degeneracyEpsilon = new Vector<float>(1e-14f);
            var simplexIsAVertex = Vector.LessThan(longestEdgeLengthSquared, degeneracyEpsilon);
            var simplexIsAnEdge = Vector.AndNot(simplexDegenerate, simplexIsAVertex);

            Vector3Wide.Dot(triangleNormal, localOffsetB, out var calibrationDot);
            var shouldCalibrate = Vector.LessThan(calibrationDot, Vector<float>.Zero);
            Vector3Wide.ConditionallyNegate(Vector.LessThan(calibrationDot, Vector<float>.Zero), ref triangleNormal);
            Swap(shouldCalibrate, ref simplex.A, ref simplex.B);

            //Just assume the triangle normal will be used for now.
            nextNormal = triangleNormal;

            Vector3Wide.CrossWithoutOverlap(simplex.B.Support, simplex.A.Support, out var bxa);
            Vector3Wide.CrossWithoutOverlap(simplex.C.Support, simplex.B.Support, out var cxb);
            Vector3Wide.CrossWithoutOverlap(simplex.A.Support, simplex.C.Support, out var axc);
            Vector3Wide.Dot(bxa, bestNormal, out var abPlaneTest);
            Vector3Wide.Dot(cxb, bestNormal, out var bcPlaneTest);
            Vector3Wide.Dot(axc, bestNormal, out var caPlaneTest);

            //Note that these tests will mark degenerate edges as containing the normal, since a degenerate plane test will be 0.
            var outsideAB = Vector.LessThan(abPlaneTest, Vector<float>.Zero);
            var outsideBC = Vector.LessThan(bcPlaneTest, Vector<float>.Zero);
            var outsideCA = Vector.LessThan(caPlaneTest, Vector<float>.Zero);
            var abDegenerate = Vector.LessThan(abLengthSquared, degeneracyEpsilon);
            var bcDegenerate = Vector.LessThan(bcLengthSquared, degeneracyEpsilon);
            var caDegenerate = Vector.LessThan(caLengthSquared, degeneracyEpsilon);
            var outsideABOrDegenerate = Vector.BitwiseOr(outsideAB, abDegenerate);
            var outsideBCOrDegenerate = Vector.BitwiseOr(outsideBC, bcDegenerate);
            var outsideCAOrDegenerate = Vector.BitwiseOr(outsideCA, caDegenerate);

            //If the best normal is not contained, then the simplex needs to search it out. We already know which edge(s) see the origin as being outside.
            //If the origin is outside two of the edge planes, use the shared vertex as a representative.
            var outsideSum = outsideAB + outsideBC + outsideCA;
            var bestNormalContained = Vector.AndNot(Vector.Equals(outsideSum, Vector<int>.Zero), simplexDegenerate);
            var useVertex = simplexIsAVertex;// Vector.BitwiseOr(simplexIsAVertex, Vector.Equals(outsideSum, new Vector<int>(-2)));
            var pushScale = new Vector<float>(2f);
            if (Vector.LessThanAny(Vector.AndNot(useVertex, terminatedLanes), Vector<int>.Zero))
            {
                //At least one lane has a vertex normal offset source.
                var useA = Vector.BitwiseAnd(outsideCAOrDegenerate, outsideABOrDegenerate);
                var useB = Vector.BitwiseAnd(outsideABOrDegenerate, outsideBCOrDegenerate);
                var useC = Vector.BitwiseAnd(outsideBCOrDegenerate, outsideCAOrDegenerate);
                Vector3Wide.ConditionalSelect(useC, simplex.C.Support, simplex.B.Support, out var vertex);
                Vector3Wide.ConditionalSelect(useA, simplex.A.Support, vertex, out vertex);
                Vector3Wide.Dot(vertex, bestNormal, out var vertexDot);
                Vector3Wide.LengthSquared(vertex, out var vertexLengthSquared);
                //TODO: Vertex can be on top of the origin and it's not a termination condition.
                Vector3Wide.Scale(vertex, vertexDot / vertexLengthSquared, out var pointOnVertexLine);
                Vector3Wide.Subtract(bestNormal, pointOnVertexLine, out var offsetToBestNormal);
                Vector3Wide.Scale(offsetToBestNormal, pushScale, out var normalPushOffset);
                //TODO: Push offset can be zero length.
                Vector3Wide.Add(normalPushOffset, bestNormal, out var nextNormalCandidate);
                Vector3Wide.ConditionalSelect(useVertex, nextNormalCandidate, nextNormal, out nextNormal);
            }
            var useEdge = Vector.AndNot(Vector.OnesComplement(bestNormalContained), useVertex);// Vector.BitwiseOr(simplexIsAnEdge, Vector.Equals(outsideSum, new Vector<int>(-1)));
            if (Vector.LessThanAny(Vector.AndNot(useEdge, terminatedLanes), Vector<int>.Zero))
            {
                //At least one lane has an edge normal offset source.
                Vector3Wide.ConditionalSelect(outsideCA, axc, bxa, out var edgePlaneNormal);
                Vector3Wide.ConditionalSelect(outsideAB, bxa, edgePlaneNormal, out edgePlaneNormal);
                Vector3Wide.Dot(edgePlaneNormal, bestNormal, out var edgeDot);
                Vector3Wide.LengthSquared(edgePlaneNormal, out var edgePlaneNormalLengthSquared);
                //The edge plane normal can't be zero length. If it was, the edge would be considered to containing the best normal.
                Vector3Wide.Scale(edgePlaneNormal, pushScale * edgeDot / edgePlaneNormalLengthSquared, out var normalPushOffset);
                //TODO: normal push offset can be zero length.
                Vector3Wide.Add(normalPushOffset, bestNormal, out var nextNormalCandidate);
                Vector3Wide.ConditionalSelect(useEdge, nextNormalCandidate, nextNormal, out nextNormal);
            }

            //Vector3Wide violatedEdgePlaneNormal;
            //violatedEdgePlaneNormal.X = Vector.ConditionalSelect(abContains, Vector.ConditionalSelect(bcContains, axc.X, cxb.X), bxa.X);
            //violatedEdgePlaneNormal.Y = Vector.ConditionalSelect(abContains, Vector.ConditionalSelect(bcContains, axc.Y, cxb.Y), bxa.Y);
            //violatedEdgePlaneNormal.Z = Vector.ConditionalSelect(abContains, Vector.ConditionalSelect(bcContains, axc.Z, cxb.Z), bxa.Z);
            //Vector3Wide.CrossWithoutOverlap(violatedEdgePlaneNormal, bestNormal, out var n);
            //Vector3Wide.CrossWithoutOverlap(n, bestNormal, out var perpendicularSearchDirection);
            //Vector3Wide.LengthSquared(perpendicularSearchDirection, out var perpendicularSearchDirectionLengthSquared);
            //var useFallback = Vector.LessThan(perpendicularSearchDirectionLengthSquared, new Vector<float>(1e-14f));

            //if (Vector.LessThanAny(Vector.AndNot(Vector.OnesComplement(useFallback), terminatedLanes), Vector<int>.Zero))
            //{
            //    //Approximate rsqrt would probably work fine here.
            //    var perpendicularSearchDirectionLengthInverse = Vector<float>.One / Vector.SquareRoot(perpendicularSearchDirectionLengthSquared);

            //    Vector3Wide.Dot(simplex.A.Normal, simplex.B.Normal, out var abNormalDot);
            //    Vector3Wide.Dot(simplex.B.Normal, simplex.C.Normal, out var bcNormalDot);
            //    Vector3Wide.Dot(simplex.C.Normal, simplex.A.Normal, out var caNormalDot);
            //    var minDot = Vector.Min(Vector.Min(abNormalDot, bcNormalDot), caNormalDot);
            //    Vector3Wide.Scale(perpendicularSearchDirection, Vector.Max(new Vector<float>(1e-1f), Vector<float>.One - minDot * minDot) * perpendicularSearchDirectionLengthInverse, out var normalPush);
            //    Vector3Wide.Add(bestNormal, normalPush, out var edgeSearchDirection);
            //    Vector3Wide.ConditionalSelect(useTriangleFace, nextNormal, edgeSearchDirection, out nextNormal);
            //}
            //if (Vector.LessThanAny(Vector.AndNot(useFallback, terminatedLanes), Vector<int>.Zero))
            //{
            //    Vector3Wide.Dot(simplex.A.Support, bestNormal, out var fallbackDot);
            //    Vector3Wide.Scale(bestNormal, fallbackDot, out var closestPointOnOriginLineToA);
            //    Vector3Wide.Subtract(closestPointOnOriginLineToA, simplex.A.Support, out var fallbackSearchDirection);
            //    Vector3Wide.LengthSquared(fallbackSearchDirection, out var fallbackLengthSquared);
            //    //If origin + bestNormal * t = support, then the search can terminate. No further progress is possible; support.A is the support associated with the minimum penetration axis.
            //    var shouldExit = Vector.LessThan(fallbackLengthSquared, new Vector<float>(1e-14f));
            //    Vector3Wide.Scale(fallbackSearchDirection, Vector<float>.One / Vector.SquareRoot(fallbackLengthSquared), out fallbackSearchDirection);
            //    Vector3Wide.Add(fallbackSearchDirection, bestNormal, out fallbackSearchDirection);
            //    Vector3Wide.ConditionalSelect(Vector.AndNot(useFallback, useTriangleFace), fallbackSearchDirection, nextNormal, out nextNormal);
            //}

            //Delete vertices that don't contribute to the latest relevant simplex feature.
            //var outsideAB = Vector.OnesComplement(abContains);
            //var outsideBC = Vector.OnesComplement(bcContains);
            //var outsideCA = Vector.OnesComplement(caContains);
            //simplex.A.Exists = Vector.ConditionalSelect(useTriangleFace, simplex.A.Exists, Vector.BitwiseOr(useFallback, Vector.BitwiseOr(outsideCA, outsideAB)));
            //simplex.B.Exists = Vector.ConditionalSelect(useTriangleFace, simplex.B.Exists, Vector.BitwiseOr(outsideAB, outsideBC));
            //simplex.C.Exists = Vector.ConditionalSelect(useTriangleFace, simplex.C.Exists, Vector.BitwiseOr(outsideBC, outsideCA));

            var abLongerThanBC = Vector.GreaterThan(abLengthSquared, bcLengthSquared);
            var abLongerThanCA = Vector.GreaterThan(abLengthSquared, caLengthSquared);
            var bcLongerThanCA = Vector.GreaterThan(bcLengthSquared, caLengthSquared);
            var abLongest = Vector.BitwiseAnd(abLongerThanBC, abLongerThanCA);
            var bcLongest = Vector.AndNot(bcLongerThanCA, abLongest);
            var caLongest = Vector.AndNot(Vector.OnesComplement(abLongest), bcLongest);

            simplex.A.Exists = Vector.ConditionalSelect(bestNormalContained, simplex.A.Exists, Vector.BitwiseOr(Vector.AndNot(Vector.BitwiseOr(outsideCA, outsideAB), simplexDegenerate), Vector.BitwiseAnd(simplexDegenerate, Vector.BitwiseOr(caLongest, abLongest))));
            simplex.B.Exists = Vector.ConditionalSelect(bestNormalContained, simplex.B.Exists, Vector.BitwiseOr(Vector.AndNot(Vector.BitwiseOr(outsideAB, outsideBC), simplexDegenerate), Vector.BitwiseAnd(simplexDegenerate, Vector.BitwiseOr(abLongest, bcLongest))));
            simplex.C.Exists = Vector.ConditionalSelect(bestNormalContained, simplex.C.Exists, Vector.BitwiseOr(Vector.AndNot(Vector.BitwiseOr(outsideBC, outsideCA), simplexDegenerate), Vector.BitwiseAnd(simplexDegenerate, Vector.BitwiseOr(bcLongest, caLongest))));

            Vector3Wide.Length(nextNormal, out var nextNormalLength);
            Vector3Wide.Scale(nextNormal, Vector<float>.One / nextNormalLength, out nextNormal);
            {
                //DEBUG STUFF
                step.NextNormalSource = bestNormalContained[0] < 0 ? SimplexTilterNormalSource.TriangleFace : useVertex[0] < 0 ? SimplexTilterNormalSource.Vertex : SimplexTilterNormalSource.Edge;

                Vector3Wide.ReadFirst(nextNormal, out step.NextNormal);
                step.ProgressionScale = progressionScale[0];
            }
            return;
            ////By now, we have a (potentially degenerate) simplex of 3 vertices.
            ////Three possible cases to consider:
            ////1) The triangle is nondegenerate and contains the origin. Containment determined by edge plane tests.
            ////2) The triangle is nondegenerate and does not contain the origin. Edge to test determined by edge plane tests.
            ////3) The triangle is degenerate. Edge to test determined by longest edge length.
            //Vector3Wide.Subtract(simplex.B.Support, simplex.A.Support, out var ab);
            //Vector3Wide.Subtract(simplex.A.Support, simplex.C.Support, out var ca);
            //Vector3Wide.CrossWithoutOverlap(ab, ca, out var triangleNormal);
            //Vector3Wide.LengthSquared(triangleNormal, out var triangleNormalLengthSquared);
            ////Compute the plane sign tests. Note that these are barycentric weights that have not been scaled by the inverse triangle normal length squared;
            ////we do not have to compute the correct magnitude to know the sign, and the sign is all we care about.
            //Vector3Wide.CrossWithoutOverlap(ab, simplex.A.Support, out var abxa);
            //Vector3Wide.CrossWithoutOverlap(ca, simplex.C.Support, out var caxc);
            //Vector3Wide.Dot(abxa, triangleNormal, out var abPlaneTest);
            //Vector3Wide.Dot(caxc, triangleNormal, out var caPlaneTest);
            //var bcPlaneTest = triangleNormalLengthSquared - caPlaneTest - abPlaneTest;
            ////Until informed otherwise, assume the origin is on the simplex face.
            //Vector3Wide.Dot(triangleNormal, localOffsetB, out var calibrationDot);
            //Vector3Wide.ConditionallyNegate(Vector.LessThan(calibrationDot, Vector<float>.Zero), ref triangleNormal);
            //nextNormal = triangleNormal;

            //{
            //    //DEBUG STUFF
            //    var inverseLengthSquared = 1f / triangleNormalLengthSquared[0];
            //    Vector3Wide.ReadFirst(simplex.A.Support, out var a);
            //    Vector3Wide.ReadFirst(simplex.B.Support, out var b);
            //    Vector3Wide.ReadFirst(simplex.C.Support, out var c);
            //    step.ClosestPointOnTriangle =
            //        a * (bcPlaneTest[0] * inverseLengthSquared) +
            //        b * (caPlaneTest[0] * inverseLengthSquared) +
            //        c * (abPlaneTest[0] * inverseLengthSquared);
            //    step.NextNormalSource = SimplexTilterNormalSource.TriangleFace;
            //}

            //Vector3Wide.Subtract(simplex.C.Support, simplex.B.Support, out var bc);
            //Vector3Wide.LengthSquared(ab, out var abLengthSquared);
            //Vector3Wide.LengthSquared(bc, out var bcLengthSquared);
            //Vector3Wide.LengthSquared(ca, out var caLengthSquared);
            //var abLongest = Vector.BitwiseAnd(Vector.GreaterThan(abLengthSquared, bcLengthSquared), Vector.GreaterThan(abLengthSquared, caLengthSquared));
            //var bcLongest = Vector.AndNot(Vector.GreaterThan(bcLengthSquared, caLengthSquared), abLongest);
            //var caLongest = Vector.AndNot(Vector.OnesComplement(abLongest), bcLongest);
            ////Area is proportional width * length. Using the longest length squared gives us an estimate for the area.
            ////The triangle normal length squared is also proportional to area, so it's a good way to threshold degeneracy.
            ////TODO: May want to use external epsilon to provide the scale or some lower bound.
            //var longestEdgeLengthSquared = Vector.ConditionalSelect(abLongest, abLengthSquared, Vector.ConditionalSelect(bcLongest, bcLengthSquared, caLengthSquared));
            //var simplexDegenerate = Vector.LessThanOrEqual(triangleNormalLengthSquared, longestEdgeLengthSquared * 1e-10f);

            //var outsideAB = Vector.LessThan(abPlaneTest, Vector<float>.Zero);
            //var outsideBC = Vector.LessThan(bcPlaneTest, Vector<float>.Zero);
            //var outsideCA = Vector.LessThan(caPlaneTest, Vector<float>.Zero);
            //var projectedOriginOutsideTriangle = Vector.BitwiseOr(outsideAB, Vector.BitwiseOr(outsideBC, outsideCA));

            //var newSampleNotImprovement = Vector.GreaterThan(depth, bestDepth);
            //var shouldContract = Vector.BitwiseAnd(Vector.AndNot(Vector.BitwiseAnd(projectedOriginOutsideTriangle, newSampleNotImprovement), simplexDegenerate), Vector.Equals(normalSource, new Vector<int>(2)));
            ////var shouldContract = Vector.BitwiseAnd(Vector.AndNot(newSampleNotImprovement, simplexDegenerate), Vector.Equals(normalSource, new Vector<int>(2)));
            //{
            //    var aBetterThanB = Vector.LessThan(simplex.A.Depth, simplex.B.Depth);
            //    var aBetterThanC = Vector.LessThan(simplex.A.Depth, simplex.C.Depth);
            //    var bBetterThanC = Vector.LessThan(simplex.B.Depth, simplex.C.Depth);
            //    var cWorst = Vector.BitwiseAnd(aBetterThanC, bBetterThanC);
            //    var bWorst = Vector.AndNot(aBetterThanB, cWorst);
            //    var aWorst = Vector.AndNot(Vector.OnesComplement(cWorst), bWorst);
            //    Vector3Wide worstNormal;
            //    worstNormal.X = Vector.ConditionalSelect(aWorst, simplex.A.Normal.X, Vector.ConditionalSelect(bWorst, simplex.B.Normal.X, simplex.C.Normal.X));
            //    worstNormal.Y = Vector.ConditionalSelect(aWorst, simplex.A.Normal.Y, Vector.ConditionalSelect(bWorst, simplex.B.Normal.Y, simplex.C.Normal.Y));
            //    worstNormal.Z = Vector.ConditionalSelect(aWorst, simplex.A.Normal.Z, Vector.ConditionalSelect(bWorst, simplex.B.Normal.Z, simplex.C.Normal.Z));
            //    Vector3Wide.Add(bestNormal, worstNormal, out var contractedNormal);
            //    //Vector3Wide.Scale(bestNormal, new Vector<float>(1f), out var weightedBest);
            //    //Vector3Wide.Add(weightedBest, worstNormal, out var contractedNormal);

            //    //Vector3Wide normalStart, normalEnd;
            //    //normalStart.X = Vector.ConditionalSelect(aWorst, simplex.B.Normal.X, Vector.ConditionalSelect(bWorst, simplex.C.Normal.X, simplex.A.Normal.X));
            //    //normalStart.Y = Vector.ConditionalSelect(aWorst, simplex.B.Normal.Y, Vector.ConditionalSelect(bWorst, simplex.C.Normal.Y, simplex.A.Normal.Y));
            //    //normalStart.Z = Vector.ConditionalSelect(aWorst, simplex.B.Normal.Z, Vector.ConditionalSelect(bWorst, simplex.C.Normal.Z, simplex.A.Normal.Z));
            //    //normalEnd.X = Vector.ConditionalSelect(aWorst, simplex.C.Normal.X, Vector.ConditionalSelect(bWorst, simplex.A.Normal.X, simplex.B.Normal.X));
            //    //normalEnd.Y = Vector.ConditionalSelect(aWorst, simplex.C.Normal.Y, Vector.ConditionalSelect(bWorst, simplex.A.Normal.Y, simplex.B.Normal.Y));
            //    //normalEnd.Z = Vector.ConditionalSelect(aWorst, simplex.C.Normal.Z, Vector.ConditionalSelect(bWorst, simplex.A.Normal.Z, simplex.B.Normal.Z));
            //    //Vector3Wide.Add(normalStart, normalEnd, out var midpoint);
            //    //Vector3Wide.Add(midpoint, worstNormal, out var contractedNormal);

            //    Vector3Wide.ConditionalSelect(shouldContract, contractedNormal, nextNormal, out nextNormal);

            //    //Delete the deepest vertex if this is a full triangle simplex that resorted to an edge test.
            //    simplex.A.Exists = Vector.BitwiseOr(Vector.AndNot(simplex.A.Exists, shouldContract), Vector.OnesComplement(Vector.BitwiseAnd(shouldContract, aWorst)));
            //    simplex.B.Exists = Vector.BitwiseOr(Vector.AndNot(simplex.B.Exists, shouldContract), Vector.OnesComplement(Vector.BitwiseAnd(shouldContract, bWorst)));
            //    simplex.C.Exists = Vector.BitwiseOr(Vector.AndNot(simplex.C.Exists, shouldContract), Vector.OnesComplement(Vector.BitwiseAnd(shouldContract, cWorst)));

            //    normalSource = Vector.ConditionalSelect(shouldContract, new Vector<int>(3), Vector<int>.Zero);
            //}



            //var useEdge = Vector.AndNot(Vector.BitwiseOr(simplexDegenerate, projectedOriginOutsideTriangle), shouldContract);
            //var activeEdgeLane = Vector.AndNot(useEdge, terminatedLanes);
            //if (Vector.LessThanAny(activeEdgeLane, Vector<int>.Zero))
            //{
            //    //At least one lane needs to perform an edge test.
            //    var testAB = Vector.BitwiseOr(Vector.AndNot(outsideAB, simplexDegenerate), Vector.BitwiseAnd(abLongest, simplexDegenerate));
            //    var testBC = Vector.BitwiseOr(Vector.AndNot(outsideBC, simplexDegenerate), Vector.BitwiseAnd(bcLongest, simplexDegenerate));

            //    Vector3Wide edgeStart, edgeOffset;
            //    edgeStart.X = Vector.ConditionalSelect(testAB, simplex.A.Support.X, Vector.ConditionalSelect(testBC, simplex.B.Support.X, simplex.C.Support.X));
            //    edgeStart.Y = Vector.ConditionalSelect(testAB, simplex.A.Support.Y, Vector.ConditionalSelect(testBC, simplex.B.Support.Y, simplex.C.Support.Y));
            //    edgeStart.Z = Vector.ConditionalSelect(testAB, simplex.A.Support.Z, Vector.ConditionalSelect(testBC, simplex.B.Support.Z, simplex.C.Support.Z));
            //    edgeOffset.X = Vector.ConditionalSelect(testAB, ab.X, Vector.ConditionalSelect(testBC, bc.X, ca.X));
            //    edgeOffset.Y = Vector.ConditionalSelect(testAB, ab.Y, Vector.ConditionalSelect(testBC, bc.Y, ca.Y));
            //    edgeOffset.Z = Vector.ConditionalSelect(testAB, ab.Z, Vector.ConditionalSelect(testBC, bc.Z, ca.Z));

            //    //TODO: If all lanes are guaranteed to be in tilt-only mode, you could branch into a divisionless version.
            //    //For example, if in AB's voronoi region, nextNormal = abcN x AB.
            //    //Using a closest point on edge implementation just shares a codepath across edge/vertex voronoi regions and tilt/GJK-style normal picking.
            //    Vector3Wide.LengthSquared(edgeOffset, out var edgeOffsetLengthSquared);
            //    //dot(origin - edgeStart, edgeOffset / ||edgeOffset||) * edgeOffset / ||edgeOffset||
            //    Vector3Wide.Dot(edgeStart, edgeOffset, out var dot);
            //    //TODO: Approximate rcp would be sufficient here.
            //    var t = -dot / edgeOffsetLengthSquared;
            //    t = Vector.Max(Vector<float>.Zero, Vector.Min(Vector<float>.One, t));
            //    //Protecting against division by zero. TODO: Don't think there's a way to guarantee min/max NaN behavior across hardware to avoid this? Doing RCP first for inf...
            //    t = Vector.ConditionalSelect(Vector.LessThan(edgeOffsetLengthSquared, new Vector<float>(1e-14f)), Vector<float>.Zero, t);
            //    Vector3Wide.Scale(edgeOffset, t, out var interpolatedOffset);
            //    Vector3Wide.Add(interpolatedOffset, edgeStart, out var closestPointOnEdge);
            //    Vector3Wide.LengthSquared(closestPointOnEdge, out var distanceSquared);
            //    var edgeNormalValid = Vector.GreaterThan(distanceSquared, new Vector<float>(1e-15f));
            //    //If the simplex is degenerate, use an interpolated fallback normal to tilt from.
            //    Vector3Wide normalStart, normalEnd;
            //    normalStart.X = Vector.ConditionalSelect(testAB, simplex.A.Normal.X, Vector.ConditionalSelect(testBC, simplex.B.Normal.X, simplex.C.Normal.X));
            //    normalStart.Y = Vector.ConditionalSelect(testAB, simplex.A.Normal.Y, Vector.ConditionalSelect(testBC, simplex.B.Normal.Y, simplex.C.Normal.Y));
            //    normalStart.Z = Vector.ConditionalSelect(testAB, simplex.A.Normal.Z, Vector.ConditionalSelect(testBC, simplex.B.Normal.Z, simplex.C.Normal.Z));
            //    normalEnd.X = Vector.ConditionalSelect(testAB, simplex.B.Normal.X, Vector.ConditionalSelect(testBC, simplex.C.Normal.X, simplex.A.Normal.X));
            //    normalEnd.Y = Vector.ConditionalSelect(testAB, simplex.B.Normal.Y, Vector.ConditionalSelect(testBC, simplex.C.Normal.Y, simplex.A.Normal.Y));
            //    normalEnd.Z = Vector.ConditionalSelect(testAB, simplex.B.Normal.Z, Vector.ConditionalSelect(testBC, simplex.C.Normal.Z, simplex.A.Normal.Z));
            //    var depthStart = Vector.ConditionalSelect(testAB, simplex.A.Depth, Vector.ConditionalSelect(testBC, simplex.B.Depth, simplex.C.Depth));
            //    var depthEnd = Vector.ConditionalSelect(testAB, simplex.B.Depth, Vector.ConditionalSelect(testBC, simplex.C.Depth, simplex.A.Depth));

            //    Vector3Wide.Scale(normalStart, Vector<float>.One - t, out var weightedStart);
            //    Vector3Wide.Scale(normalEnd, t, out var weightedEnd);
            //    Vector3Wide.Add(weightedStart, weightedEnd, out var interpolatedNormal);

            //    Vector3Wide.ConditionalSelect(simplexDegenerate, interpolatedNormal, triangleNormal, out var tiltStart);


            //    //Tilt the normal toward the origin.

            //    //Closest point to the origin on the plane of tiltStart and closestPointOnEdge:
            //    //tiltStart * dot(closestPointOnEdge - origin, tiltStart) / ||tiltStart||^2
            //    //Vector3Wide.Dot(closestPointOnEdge, bestNormal, out var tiltPlaneDot);
            //    //Vector3Wide.Scale(bestNormal, tiltPlaneDot, out var pointOnPlane);
            //    //Vector3Wide.Subtract(pointOnPlane, closestPointOnEdge, out var tiltOffset);
            //    //Vector3Wide.Length(tiltOffset, out var tiltOffsetLength);

            //    Vector3Wide.Dot(closestPointOnEdge, tiltStart, out var tiltPlaneDot);
            //    Vector3Wide.LengthSquared(tiltStart, out var tiltStartLengthSquared);
            //    Vector3Wide.Scale(tiltStart, tiltPlaneDot / tiltStartLengthSquared, out var pointOnPlane);
            //    Vector3Wide.Subtract(pointOnPlane, closestPointOnEdge, out var tiltOffset);
            //    Vector3Wide.Length(tiltOffset, out var tiltOffsetLength);

            //    //var useStart = Vector.LessThan(t, new Vector<float>(0.5f));
            //    //Vector3Wide.ConditionalSelect(useStart, normalStart, normalEnd, out var originLineNormal);
            //    //var originLineDepth = Vector.ConditionalSelect(useStart, depthStart, depthEnd);
            //    Vector3Wide.Scale(bestNormal, tiltOffsetLength * progressionScale, out var originLineNormalOffset);
            //    Vector3Wide.Subtract(pointOnPlane, originLineNormalOffset, out var tiltTargetPoint);

            //    Vector3Wide.Subtract(tiltTargetPoint, closestPointOnEdge, out var triangleToOriginLine);
            //    Vector3Wide.CrossWithoutOverlap(triangleToOriginLine, tiltStart, out var n);
            //    Vector3Wide.CrossWithoutOverlap(n, triangleToOriginLine, out var tiltedNormal);
            //    Vector3Wide.ConditionalSelect(useEdge, tiltedNormal, triangleNormal, out nextNormal);

            //    //REFLECT ORIGIN OFFSET OVER NORMAL
            //    //Vector3Wide.Dot(closestPointOnEdge, triangleNormal, out var bestPlaneDot);
            //    //Vector3Wide.Scale(triangleNormal, bestPlaneDot * 2f / triangleNormalLengthSquared, out var reflectionOffset);
            //    //Vector3Wide.Subtract(reflectionOffset, closestPointOnEdge, out var reflectedNormal);
            //    //Vector3Wide.ConditionalSelect(simplexDegenerate, tiltedNormal, reflectedNormal, out var edgeNormal);
            //    //Vector3Wide.ConditionalSelect(useEdge, edgeNormal, triangleNormal, out nextNormal);

            //    //ALWAYS TRIANGLE NORMAL IF AVAILABLE
            //    //Vector3Wide.ConditionalSelect(simplexDegenerate, tiltedNormal, triangleNormal, out nextNormal);

            //    //BARYCENTRIC
            //    //var inverse = Vector<float>.One / triangleNormalLengthSquared;
            //    //var aWeight = bcPlaneTest * inverse;
            //    //var bWeight = caPlaneTest * inverse;
            //    //var cWeight = abPlaneTest * inverse;
            //    //Vector3Wide.Scale(simplex.A.Normal, aWeight, out var weightedA);
            //    //Vector3Wide.Scale(simplex.B.Normal, bWeight, out var weightedB);
            //    //Vector3Wide.Scale(simplex.C.Normal, cWeight, out var weightedC);
            //    //Vector3Wide.Add(weightedA, weightedB, out var barycentricNormal);
            //    //Vector3Wide.Add(weightedC, barycentricNormal, out barycentricNormal);
            //    //Vector3Wide.ConditionalSelect(simplexDegenerate, tiltedNormal, barycentricNormal, out var edgeNormal);
            //    //Vector3Wide.ConditionalSelect(useEdge, edgeNormal, triangleNormal, out nextNormal);


            //    //NELDER MEAD STYLE REFLECT OVER CLOSEST EDGE
            //    Vector3Wide.Add(normalStart, normalEnd, out var scaledMidpoint);
            //    Vector3Wide opposingNormal;
            //    opposingNormal.X = Vector.ConditionalSelect(testAB, simplex.C.Normal.X, Vector.ConditionalSelect(testBC, simplex.A.Normal.X, simplex.B.Normal.X));
            //    opposingNormal.Y = Vector.ConditionalSelect(testAB, simplex.C.Normal.Y, Vector.ConditionalSelect(testBC, simplex.A.Normal.Y, simplex.B.Normal.Y));
            //    opposingNormal.Z = Vector.ConditionalSelect(testAB, simplex.C.Normal.Z, Vector.ConditionalSelect(testBC, simplex.A.Normal.Z, simplex.B.Normal.Z));
            //    Vector3Wide.Scale(opposingNormal, new Vector<float>(2), out var scaledOpposingNormal);
            //    Vector3Wide.Subtract(scaledMidpoint, scaledOpposingNormal, out var offset);
            //    Vector3Wide.Add(opposingNormal, offset, out var reflectedNormal);
            //    Vector3Wide.ConditionalSelect(simplexDegenerate, tiltedNormal, reflectedNormal, out var edgeNormal);
            //    Vector3Wide.ConditionalSelect(activeEdgeLane, edgeNormal, nextNormal, out nextNormal);

            //    //NELDER MEAD STYLE REFLECT
            //    //var aBetterThanB = Vector.LessThan(simplex.A.Depth, simplex.B.Depth);
            //    //var aBetterThanC = Vector.LessThan(simplex.A.Depth, simplex.C.Depth);
            //    //var bBetterThanC = Vector.LessThan(simplex.B.Depth, simplex.C.Depth);
            //    //var cWorst = Vector.BitwiseAnd(aBetterThanC, bBetterThanC);
            //    //var bWorst = Vector.AndNot(aBetterThanB, cWorst);
            //    //var aWorst = Vector.AndNot(Vector.OnesComplement(cWorst), bWorst);
            //    //var aBest = Vector.BitwiseAnd(aBetterThanB, aBetterThanC);
            //    //var bBest = Vector.AndNot(bBetterThanC, aBest);
            //    //var cBest = Vector.AndNot(Vector.OnesComplement(aBest), bBest);
            //    //var aMedium = Vector.AndNot(Vector.OnesComplement(aWorst), aBest);
            //    //var bMedium = Vector.AndNot(Vector.OnesComplement(bWorst), bBest);
            //    //var cMedium = Vector.AndNot(Vector.OnesComplement(cWorst), cBest);
            //    //Vector3Wide minNormal, midNormal, maxNormal;
            //    //maxNormal.X = Vector.ConditionalSelect(aWorst, simplex.A.Normal.X, Vector.ConditionalSelect(bWorst, simplex.B.Normal.X, simplex.C.Normal.X));
            //    //maxNormal.Y = Vector.ConditionalSelect(aWorst, simplex.A.Normal.Y, Vector.ConditionalSelect(bWorst, simplex.B.Normal.Y, simplex.C.Normal.Y));
            //    //maxNormal.Z = Vector.ConditionalSelect(aWorst, simplex.A.Normal.Z, Vector.ConditionalSelect(bWorst, simplex.B.Normal.Z, simplex.C.Normal.Z));

            //    //midNormal.X = Vector.ConditionalSelect(aMedium, simplex.A.Normal.X, Vector.ConditionalSelect(bMedium, simplex.B.Normal.X, simplex.C.Normal.X));
            //    //midNormal.Y = Vector.ConditionalSelect(aMedium, simplex.A.Normal.Y, Vector.ConditionalSelect(bMedium, simplex.B.Normal.Y, simplex.C.Normal.Y));
            //    //midNormal.Z = Vector.ConditionalSelect(aMedium, simplex.A.Normal.Z, Vector.ConditionalSelect(bMedium, simplex.B.Normal.Z, simplex.C.Normal.Z));

            //    //minNormal.X = Vector.ConditionalSelect(aBest, simplex.A.Normal.X, Vector.ConditionalSelect(bWorst, simplex.B.Normal.X, simplex.C.Normal.X));
            //    //minNormal.Y = Vector.ConditionalSelect(aBest, simplex.A.Normal.Y, Vector.ConditionalSelect(bWorst, simplex.B.Normal.Y, simplex.C.Normal.Y));
            //    //minNormal.Z = Vector.ConditionalSelect(aBest, simplex.A.Normal.Z, Vector.ConditionalSelect(bWorst, simplex.B.Normal.Z, simplex.C.Normal.Z));

            //    //Vector3Wide.Add(minNormal, midNormal, out var scaledMidpoint);
            //    //Vector3Wide.Scale(maxNormal, new Vector<float>(2), out var scaledOpposingNormal);
            //    //Vector3Wide.Subtract(scaledMidpoint, scaledOpposingNormal, out var offset);
            //    //Vector3Wide.Add(maxNormal, offset, out var reflectedNormal);
            //    //Vector3Wide.ConditionalSelect(simplexDegenerate, tiltedNormal, reflectedNormal, out var edgeNormal);
            //    //Vector3Wide.ConditionalSelect(activeEdgeLane, edgeNormal, nextNormal, out nextNormal);

            //    ////Delete the deepest vertex if this is a full triangle simplex that resorted to an edge test.
            //    //var useTriangle = Vector.OnesComplement(useEdge);
            //    //simplex.A.Exists = Vector.BitwiseOr(useTriangle, Vector.OnesComplement(aWorst));
            //    //simplex.B.Exists = Vector.BitwiseOr(useTriangle, Vector.OnesComplement(bWorst));
            //    //simplex.C.Exists = Vector.BitwiseOr(useTriangle, Vector.OnesComplement(cWorst));

            //    //REMOVE WORST
            //    //var useTriangle = Vector.OnesComplement(useEdge);
            //    //var aWorst = Vector.BitwiseAnd(Vector.GreaterThan(simplex.A.Depth, simplex.B.Depth), Vector.GreaterThan(simplex.A.Depth, simplex.C.Depth));
            //    //var bWorst = Vector.AndNot(Vector.GreaterThan(simplex.B.Depth, simplex.C.Depth), aWorst);
            //    //var cWorst = Vector.AndNot(Vector.OnesComplement(aWorst), bWorst);
            //    //var notDegenerate = Vector.OnesComplement(simplexDegenerate);
            //    ////Delete the deepest vertex if this is a full triangle simplex that resorted to an edge test.
            //    //simplex.A.Exists = Vector.BitwiseOr(useTriangle, Vector.OnesComplement(aWorst));
            //    //simplex.B.Exists = Vector.BitwiseOr(useTriangle, Vector.OnesComplement(bWorst));
            //    //simplex.C.Exists = Vector.BitwiseOr(useTriangle, Vector.OnesComplement(cWorst));

            //    //Delete any uninvolved simplex entries.
            //    //var testCA = Vector.AndNot(Vector.OnesComplement(testAB), testBC);
            //    //simplex.A.Exists = Vector.ConditionalSelect(useEdge,
            //    //    Vector.BitwiseOr(
            //    //        Vector.BitwiseAnd(testCA, Vector.GreaterThan(t, Vector<float>.Zero)),
            //    //        Vector.BitwiseAnd(testAB, Vector.LessThan(t, Vector<float>.One))), simplex.A.Exists);
            //    //simplex.B.Exists = Vector.ConditionalSelect(useEdge,
            //    //    Vector.BitwiseOr(
            //    //        Vector.BitwiseAnd(testAB, Vector.GreaterThan(t, Vector<float>.Zero)),
            //    //        Vector.BitwiseAnd(testBC, Vector.LessThan(t, Vector<float>.One))), simplex.B.Exists);
            //    //simplex.C.Exists = Vector.ConditionalSelect(useEdge,
            //    //    Vector.BitwiseOr(
            //    //        Vector.BitwiseAnd(testBC, Vector.GreaterThan(t, Vector<float>.Zero)),
            //    //        Vector.BitwiseAnd(testCA, Vector.LessThan(t, Vector<float>.One))), simplex.C.Exists);

            //    var testCA = Vector.AndNot(Vector.OnesComplement(testAB), testBC);
            //    simplex.A.Exists = Vector.ConditionalSelect(useEdge, Vector.BitwiseOr(testCA, testAB), simplex.A.Exists);
            //    simplex.B.Exists = Vector.ConditionalSelect(useEdge, Vector.BitwiseOr(testAB, testBC), simplex.B.Exists);
            //    simplex.C.Exists = Vector.ConditionalSelect(useEdge, Vector.BitwiseOr(testBC, testCA), simplex.C.Exists);

            //    normalSource = Vector.ConditionalSelect(useEdge, Vector.ConditionalSelect(simplexDegenerate, Vector<int>.One, new Vector<int>(2)), normalSource);

            //    {
            //        //DEBUG STUFF
            //        //step.UsingReflection = step.EdgeCase && simplexDegenerate[0] == 0; 
            //        Vector3Wide.ReadFirst(tiltOffset, out step.TiltOffset);
            //        Vector3Wide.ReadFirst(closestPointOnEdge, out step.ClosestPointOnTriangle);
            //        Vector3Wide.ReadFirst(tiltStart, out step.TiltStart);
            //        Vector3Wide.ReadFirst(tiltTargetPoint, out step.TiltTargetPoint);
            //    }
            //}
            //Vector3Wide.Length(nextNormal, out var nextNormalLength);
            //Vector3Wide.Scale(nextNormal, Vector<float>.One / nextNormalLength, out nextNormal);
            //{
            //    //DEBUG STUFF
            //    Vector3Wide.ReadFirst(nextNormal, out step.NextNormal);
            //    step.NextNormalSource = (SimplexTilterNormalSource)normalSource[0];
            //    step.ProgressionScale = progressionScale[0];
            //}
        }


        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void FindMinimumDepth(in TShapeWideA a, in TShapeWideB b, in Vector3Wide localOffsetB, in Matrix3x3Wide localOrientationB, ref TSupportFinderA supportFinderA, ref TSupportFinderB supportFinderB,
            in Vector3Wide initialNormal, in Vector<int> inactiveLanes, in Vector<float> convergenceThreshold, in Vector<float> minimumDepthThreshold, out Vector<float> depth, out Vector3Wide refinedNormal, List<SimplexTilterStep> steps, int maximumIterations = 50)
        {
#if DEBUG
            Vector3Wide.LengthSquared(initialNormal, out var initialNormalLengthSquared);
            Debug.Assert(Vector.LessThanAll(Vector.BitwiseOr(inactiveLanes, Vector.LessThan(Vector.Abs(initialNormalLengthSquared - Vector<float>.One), new Vector<float>(1e-6f))), Vector<int>.Zero));
#endif
            FindSupport(a, b, localOffsetB, localOrientationB, ref supportFinderA, ref supportFinderB, initialNormal, out var initialSupport);
            Vector3Wide.Dot(initialSupport, initialNormal, out var initialDepth);
            Create(initialNormal, initialSupport, initialDepth, out var simplex);
            FindMinimumDepth(a, b, localOffsetB, localOrientationB, ref supportFinderA, ref supportFinderB, ref simplex, initialDepth, inactiveLanes, convergenceThreshold, minimumDepthThreshold, out depth, out refinedNormal, steps, maximumIterations);
        }

        public static void FindMinimumDepth(in TShapeWideA a, in TShapeWideB b, in Vector3Wide localOffsetB, in Matrix3x3Wide localOrientationB, ref TSupportFinderA supportFinderA, ref TSupportFinderB supportFinderB,
            ref Simplex simplex, in Vector<float> initialDepth,
            in Vector<int> inactiveLanes, in Vector<float> surfaceThreshold, in Vector<float> minimumDepthThreshold, out Vector<float> refinedDepth, out Vector3Wide refinedNormal, List<SimplexTilterStep> steps, int maximumIterations = 50)
        {
#if DEBUG
            Vector3Wide.LengthSquared(simplex.A.Normal, out var initialNormalLengthSquared);
            Debug.Assert(Vector.LessThanAll(Vector.BitwiseOr(inactiveLanes, Vector.LessThan(Vector.Abs(initialNormalLengthSquared - Vector<float>.One), new Vector<float>(1e-6f))), Vector<int>.Zero));
#endif            
            var depthBelowThreshold = Vector.LessThan(initialDepth, minimumDepthThreshold);
            var terminatedLanes = Vector.BitwiseOr(depthBelowThreshold, inactiveLanes);

            refinedNormal = simplex.A.Normal;
            refinedDepth = initialDepth;
            if (Vector.LessThanAll(terminatedLanes, Vector<int>.Zero))
            {
                return;
            }

            var progressionScale = new Vector<float>(0.5f);
            GetNextNormal<HasNoNewSupport>(ref simplex, localOffsetB, default, default, default, ref progressionScale, terminatedLanes, refinedNormal, refinedDepth, out var normal, out var debugStep);
            debugStep.BestDepth = refinedDepth[0];
            Vector3Wide.ReadSlot(ref refinedNormal, 0, out debugStep.BestNormal);
            steps.Add(debugStep);

            for (int i = 0; i < maximumIterations; ++i)
            {
                FindSupport(a, b, localOffsetB, localOrientationB, ref supportFinderA, ref supportFinderB, normal, out var support);
                Vector3Wide.Dot(support, normal, out var depth);

                var useNewDepth = Vector.LessThan(depth, refinedDepth);
                refinedDepth = Vector.ConditionalSelect(useNewDepth, depth, refinedDepth);
                Vector3Wide.ConditionalSelect(useNewDepth, normal, refinedNormal, out refinedNormal);
                //progressionScale = Vector.ConditionalSelect(useNewDepth, progressionScale, progressionScale * 0.85f);

                GetNextNormal<HasNewSupport>(ref simplex, localOffsetB, support, normal, depth, ref progressionScale, terminatedLanes, refinedNormal, refinedDepth, out normal, out debugStep);

                debugStep.BestDepth = refinedDepth[0];
                Vector3Wide.ReadSlot(ref refinedNormal, 0, out debugStep.BestNormal);

                steps.Add(debugStep);
            }
        }
    }
}