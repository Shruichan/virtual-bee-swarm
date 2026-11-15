using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using VirtualBeeSwarm.Bloom;

namespace VirtualBeeSwarm.AR.Tests
{
    /// <summary>EditMode tests for the AR room model (PLAN.md V3 "done when"): every landing spot is on the table and
    /// none is inside a footprint, for the fake source and for a placed table; plus the tuning parser and the fake
    /// raycaster that the Linux autopilot depends on.</summary>
    public class RoomModelTests
    {
        GameObject _anchor;

        [SetUp]
        public void SetUp()
        {
            _anchor = new GameObject("TableAnchor (test)");
            _anchor.transform.position = new Vector3(0f, 0.75f, 0f);
        }

        [TearDown]
        public void TearDown()
        {
            Object.DestroyImmediate(_anchor);
            ArTuning.Load();
        }

        static void AssertSpots(RoomModel room, int atLeast)
        {
            Assert.GreaterOrEqual(room.TableSpotCount, atLeast, room.Describe());
            Assert.LessOrEqual(room.TableSpotCount, RoomModel.MaxTableSpots);
            for (int i = 0; i < room.TableSpotCount; i++)
            {
                var p = room.TableSpots[i];
                Assert.IsTrue(room.InsideTable(p), "spot " + i + " " + p + " is off the table");
                Assert.IsFalse(room.InsideFootprint(p), "spot " + i + " " + p + " is inside a footprint");
                Assert.AreEqual(room.Table.Y, p.y, 1e-4f, "spot " + i + " is not on the top");
                for (int j = 0; j < i; j++)
                    Assert.Greater((room.TableSpots[j] - p).magnitude, 0.035f, "spots " + j + " and " + i + " overlap");
            }
        }

        [Test]
        public void FakeRoom_SpotsOnTableOutsideFootprints()
        {
            var room = new RoomModel();
            FakeRoomSource.Build(room, null, _anchor.transform);
            Assert.IsTrue(room.HasTable);
            Assert.IsTrue(room.HasWall);
            Assert.AreEqual(0.75f, room.Table.Y, 1e-4f);
            Assert.AreEqual(RoomModel.MaxTableSpots, room.TableSpotCount, room.Describe());
            AssertSpots(room, RoomModel.MaxTableSpots);
            // the wall is 1.5 m behind the back edge, facing the table
            Assert.AreEqual(0.1f + 0.4f + FakeRoomSource.WallBehindTable, room.WindowWall.point.z, 1e-4f);
            Assert.Less(room.WindowWall.normal.z, -0.99f);
        }

        [Test]
        public void PlacedTable_RotatedAndRaised_SpotsStillValid()
        {
            _anchor.transform.SetPositionAndRotation(new Vector3(1.2f, 0.92f, -0.4f), Quaternion.Euler(0f, 37f, 0f));
            var room = new RoomModel();
            PlacementRoomSource.Build(room, _anchor.transform, 0.22f, default, 0f);
            Assert.IsTrue(room.HasTable);
            Assert.IsFalse(room.HasWall);
            Assert.AreEqual(0.92f, room.Table.Y, 1e-4f);
            AssertSpots(room, 8);
            // the front edge is `inset` in front of the anchor
            Vector3 front = room.TableToWorld(0f, -room.Table.half.y);
            Assert.AreEqual(0.22f, Vector3.Distance(front, _anchor.transform.position), 1e-3f);
        }

        [Test]
        public void ClampAboveTable_KeepsBeesOnTop()
        {
            var room = new RoomModel();
            FakeRoomSource.Build(room, null, _anchor.transform);
            var p = new Vector3(0.5f, 0.70f, 0.1f);
            float d = room.ClampAboveTable(ref p, 0.01f);
            Assert.AreEqual(0.76f, p.y, 1e-4f);
            Assert.Greater(d, 0f);
            var under = new Vector3(0.5f, 0.3f, 0.1f); // under the table: left alone
            Assert.AreEqual(0f, room.ClampAboveTable(ref under));
            var off = new Vector3(3f, 0.5f, 0.1f); // not over the table
            Assert.AreEqual(0f, room.ClampAboveTable(ref off));
        }

        [Test]
        public void FakeRaycaster_HitsTableWallFloor()
        {
            var room = new RoomModel();
            FakeRoomSource.Build(room, null, _anchor.transform);
            var rc = new FakeEnvRaycaster(room) { Enabled = true };
            Assert.IsTrue(rc.Raycast(new Ray(new Vector3(0f, 1.05f, 0.15f), Vector3.down), 1f, out var down));
            Assert.AreEqual(0.75f, down.point.y, 1e-4f);
            Assert.Greater(down.normal.y, 0.9f);
            Assert.IsTrue(rc.Raycast(new Ray(new Vector3(0f, 1.4f, -0.8f), Vector3.forward), 5f, out var wall));
            Assert.AreEqual(2.0f, wall.point.z, 1e-4f);
            Assert.Less(Mathf.Abs(wall.normal.y), 0.2f);
            Assert.IsTrue(rc.Raycast(new Ray(new Vector3(2f, 1.4f, 0f), Vector3.down), 5f, out var floor));
            Assert.AreEqual(0f, floor.point.y, 1e-4f);
            Assert.IsTrue(rc.PlaceBox(new Ray(new Vector3(0f, 1.4f, -0.5f), (new Vector3(0f, 0.75f, 0.1f) - new Vector3(0f, 1.4f, -0.5f)).normalized),
                new Vector3(0.6f, 0.45f, 0.3f), Vector3.forward, out var place));
            Assert.AreEqual(0.75f, place.point.y, 1e-4f);
            // too close to the edge: the 0.6 m box doesn't fit
            Assert.IsFalse(rc.PlaceBox(new Ray(new Vector3(0.6f, 1.4f, 0f), Vector3.down), new Vector3(0.6f, 0.45f, 0.3f), Vector3.forward, out _));
            rc.Enabled = false;
            Assert.IsFalse(rc.Raycast(new Ray(Vector3.up, Vector3.down), 5f, out var off));
            Assert.AreEqual(ArHitStatus.NotReady, off.status);
        }

        [Test]
        public void FloorPoint_SnapsNoisyDepthHitToTheFloor()
        {
            const float floorY = 0.25f;
            var ray = new Ray(new Vector3(0.5f, 1.5f, 0.75f), Vector3.down);
            var hit = new ArHit
            {
                status = ArHitStatus.Hit,
                point = new Vector3(0.5f, floorY + 0.12f, 0.75f),
                normal = Vector3.up
            };

            Assert.IsTrue(InstantPlacement.TryFloorPoint(ray, true, hit, floorY, 4f, true, out var point));
            Assert.AreEqual(new Vector3(0.5f, floorY, 0.75f), point);
        }

        [TestCase(ArHitStatus.NoHit)]
        [TestCase(ArHitStatus.NotReady)]
        [TestCase(ArHitStatus.NotSupported)]
        public void FloorPoint_UsesKnownFloorWithoutDepth(ArHitStatus status)
        {
            const float floorY = 0.25f;
            var ray = new Ray(new Vector3(0.5f, 1.5f, 0.75f), Vector3.down);
            var miss = new ArHit { status = status };

            Assert.IsTrue(InstantPlacement.TryFloorPoint(ray, false, miss, floorY, 4f, true, out var point));
            Assert.AreEqual(new Vector3(0.5f, floorY, 0.75f), point);
            Assert.IsFalse(InstantPlacement.TryFloorPoint(ray, false, miss, floorY, 4f, false, out _),
                "an unknown floor cannot be guessed when depth is unavailable");
        }

        [Test]
        public void FloorPoint_RejectsWallTableOcclusionAndInvalidAim()
        {
            var ray = new Ray(new Vector3(0f, 1.5f, 0f), Vector3.down);
            var wall = new ArHit { status = ArHitStatus.Hit, point = Vector3.zero, normal = Vector3.forward };
            var table = new ArHit { status = ArHitStatus.Hit, point = Vector3.up * 0.75f, normal = Vector3.up };
            var occluded = new ArHit { status = ArHitStatus.Occluded };
            var miss = new ArHit { status = ArHitStatus.NoHit };
            Assert.IsFalse(InstantPlacement.TryFloorPoint(ray, true, wall, 0f, 4f, true, out _));
            Assert.IsFalse(InstantPlacement.TryFloorPoint(ray, true, table, 0f, 4f, true, out _));
            Assert.IsFalse(InstantPlacement.TryFloorPoint(ray, false, occluded, 0f, 4f, true, out _));
            Assert.IsFalse(InstantPlacement.TryFloorPoint(ray, false, miss, float.NaN, 4f, true, out _));
            Assert.IsFalse(InstantPlacement.TryFloorPoint(ray, false, miss, 0f, 1f, true, out _));
            Assert.IsFalse(InstantPlacement.TryFloorPoint(new Ray(ray.origin, Vector3.forward), false, miss, 0f, 4f, true, out _));
            Assert.IsFalse(InstantPlacement.TryFloorPoint(new Ray(ray.origin, Vector3.up), false, miss, 0f, 4f, true, out _));
        }

        [Test]
        public void ClearFloor_RejectsOnlyFootprintsCrossingScannedEdgesOrFurniture()
        {
            var scan = new BloomScene();
            scan.FloorPolygon.AddRange(new[] { new Vector2(-1f, -1f), new Vector2(1f, -1f),
                new Vector2(1f, 1f), new Vector2(-1f, 1f) });
            var footprint = new Vector2(0.42f, 0.36f);
            Vector3 diagonal = (Vector3.right + Vector3.forward).normalized;
            var nearEdge = new Vector3(0.80f, 0f, 0f);

            Assert.IsTrue(InstantPlacement.IsClearFloor(scan, Vector3.zero, diagonal, footprint));
            Assert.IsTrue(scan.InFloor(nearEdge), "the center remains inside the scan");
            Assert.IsFalse(InstantPlacement.IsClearFloor(scan, nearEdge, diagonal, footprint));

            scan.Volumes.Add(new BloomBox
            {
                center = new Vector3(0.21f, 0.6f, 0f),
                rotation = Quaternion.identity,
                half = new Vector3(0.045f, 0.10f, 0.075f)
            });
            Assert.IsFalse(scan.InVolume(Vector3.zero, 0.04f), "the center alone has room");
            Assert.IsFalse(InstantPlacement.IsClearFloor(scan, Vector3.zero, Vector3.forward, footprint));
            Assert.IsTrue(InstantPlacement.IsClearFloor(scan, Vector3.left * 0.5f, Vector3.forward, footprint));
        }

        [Test]
        public void PortalSightline_RemovesOnlyFoliageProjectedIntoTheOpening()
        {
            var window = new BloomRect
            {
                center = new Vector3(0f, 1.5f, 2f),
                right = Vector3.right,
                up = Vector3.up,
                normal = Vector3.back,
                half = new Vector2(0.5f, 0.3f)
            };
            var eye = new Vector3(0f, 1.5f, 0f);

            Assert.IsTrue(MeadowWalls.BlocksPortalView(window, eye, new Vector3(0.10f, 1.55f, 1f)),
                "foliage in front whose sightline crosses the aperture is cleared");
            Assert.IsFalse(MeadowWalls.BlocksPortalView(window, eye, new Vector3(0.45f, 1.5f, 1f)),
                "perspective puts this foliage beside the aperture");
            Assert.IsFalse(MeadowWalls.BlocksPortalView(window, eye, new Vector3(0f, 2f, 1f)),
                "foliage seen above the aperture stays in place");
            Assert.IsFalse(MeadowWalls.BlocksPortalView(window, eye, new Vector3(0.10f, 1.55f, 2.35f)),
                "foliage behind the portal plane and its trim stays in place");
        }

        [Test]
        public void Tuning_ParsesNestedAndFlatKeys()
        {
            var flat = new Dictionary<string, string>();
            ArTuning.FlatJson.Parse("{\"ar\":{\"occlusion\":\"soft\",\"landing\":false,\"list\":[1,{\"a\":2}]},\"ar.ceilingY\":2.7,\"delivery\":{\"mode\":\"swirl\"}}", flat);
            Assert.AreEqual("soft", flat["ar.occlusion"]);
            Assert.AreEqual("false", flat["ar.landing"]);
            Assert.AreEqual("2.7", flat["ar.ceilingY"]);
            Assert.AreEqual("swirl", flat["delivery.mode"]);
            Assert.IsTrue(ArTuning.Set("ar.occlusion", "hard"));
            Assert.AreEqual(ArTuning.Occlusion.Hard, ArTuning.OcclusionMode);
            Assert.IsTrue(ArTuning.Set("windowOnWall", "off"));
            Assert.IsFalse(ArTuning.WindowOnWall);
            Assert.IsFalse(ArTuning.Set("ar.ceilingY", "99"));
        }
    }
}
