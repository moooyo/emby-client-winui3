using EmbyClient.App.Playback;
using Xunit;

namespace EmbyClient.Platform.Tests;

public sealed class AssSubtitleLayoutPolicyTests
{
    [Fact]
    public void FourByThreeVideoCentersBetweenSideBarsInAWideViewport()
    {
        var geometry = new AssSubtitleGeometry(640, 480, 1, 64, 96, 128, 48);

        var placement = Place(1920, 1080, geometry);

        AssertRectangle(placement.Video, 240, 0, 1440, 1080);
        AssertRectangle(placement.Image, 384, 216, 288, 108);
    }

    [Fact]
    public void WideVideoCentersBetweenTopAndBottomBars()
    {
        var geometry = new AssSubtitleGeometry(1920, 800, 1, 240, 720, 480, 80);

        var placement = Place(1920, 1080, geometry);

        AssertRectangle(placement.Video, 0, 140, 1920, 800);
        AssertRectangle(placement.Image, 240, 860, 480, 80);
    }

    [Fact]
    public void LandscapeVideoAndItsCropRemainCenteredInAPortraitViewport()
    {
        var geometry = new AssSubtitleGeometry(1920, 1080, 1, 240, 900, 480, 90);

        var placement = Place(600, 1000, geometry);

        AssertRectangle(placement.Video, 0, 331.25, 600, 337.5);
        AssertRectangle(placement.Image, 75, 612.5, 150, 28.125);
    }

    [Fact]
    public void WiderPixelsDetermineDisplayAspectWhileTheCropUsesNativeCoordinates()
    {
        var geometry = new AssSubtitleGeometry(720, 576, 16d / 15d, 90, 432, 180, 72);

        var placement = Place(1024, 768, geometry);

        AssertRectangle(placement.Video, 0, 0, 1024, 768);
        AssertRectangle(placement.Image, 128, 576, 256, 96);
    }

    [Fact]
    public void NarrowerPixelsProduceSideBarsAndCorrectHorizontalCropPlacement()
    {
        var geometry = new AssSubtitleGeometry(720, 576, 8d / 9d, 90, 432, 180, 72);

        var placement = Place(1280, 720, geometry);

        AssertRectangle(placement.Video, 240, 0, 800, 720);
        AssertRectangle(placement.Image, 340, 540, 200, 90);
    }

    [Theory]
    [InlineData(0, 0, 640, 480, 0, 0, 1280, 960)]
    [InlineData(320, 240, 320, 240, 640, 480, 640, 480)]
    [InlineData(639, 479, 1, 1, 1278, 958, 2, 2)]
    public void CropsMayReachEveryVideoEdgeWithoutAnExtraVerticalOffset(
        int x, int y, int width, int height,
        double expectedX, double expectedY, double expectedWidth, double expectedHeight)
    {
        var geometry = new AssSubtitleGeometry(640, 480, 1, x, y, width, height);

        var placement = Place(1280, 960, geometry);

        AssertRectangle(placement.Video, 0, 0, 1280, 960);
        AssertRectangle(placement.Image, expectedX, expectedY, expectedWidth, expectedHeight);
    }

    [Fact]
    public void ResizingRecalculatesTheCropFromTheOriginalPixels()
    {
        var geometry = new AssSubtitleGeometry(1920, 1080, 1, 120, 60, 360, 180);

        var fullSize = Place(1920, 1080, geometry);
        var halfSize = Place(960, 540, geometry);

        AssertRectangle(fullSize.Image, 120, 60, 360, 180);
        AssertRectangle(halfSize.Image, 60, 30, 180, 90);
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    [InlineData(0)]
    [InlineData(-1)]
    public void InvalidViewportDimensionsReturnNoPlacement(double dimension)
    {
        var geometry = new AssSubtitleGeometry(640, 480, 1, 0, 0, 640, 480);

        Assert.False(AssSubtitleLayoutPolicy.TryPlace(dimension, 1080, geometry, out var invalidWidth));
        Assert.Equal(default, invalidWidth);
        Assert.False(AssSubtitleLayoutPolicy.TryPlace(1920, dimension, geometry, out var invalidHeight));
        Assert.Equal(default, invalidHeight);
    }

    [Theory]
    [InlineData(0, 480, 1, 0, 0, 1, 1)]
    [InlineData(640, 0, 1, 0, 0, 1, 1)]
    [InlineData(-640, 480, 1, 0, 0, 1, 1)]
    [InlineData(640, -480, 1, 0, 0, 1, 1)]
    [InlineData(640, 480, 1, -1, 0, 1, 1)]
    [InlineData(640, 480, 1, 0, -1, 1, 1)]
    [InlineData(640, 480, 1, 0, 0, 0, 1)]
    [InlineData(640, 480, 1, 0, 0, 1, 0)]
    [InlineData(640, 480, 1, 0, 0, -1, 1)]
    [InlineData(640, 480, 1, 0, 0, 1, -1)]
    [InlineData(640, 480, 1, 600, 0, 41, 1)]
    [InlineData(640, 480, 1, 0, 440, 1, 41)]
    [InlineData(640, 480, 1, 0, 0, 641, 1)]
    [InlineData(640, 480, 1, 0, 0, 1, 481)]
    [InlineData(640, 480, double.NaN, 0, 0, 1, 1)]
    [InlineData(640, 480, double.PositiveInfinity, 0, 0, 1, 1)]
    [InlineData(640, 480, double.NegativeInfinity, 0, 0, 1, 1)]
    [InlineData(640, 480, 0, 0, 0, 1, 1)]
    [InlineData(640, 480, -1, 0, 0, 1, 1)]
    [InlineData(2, 2, double.MaxValue, 0, 0, 1, 1)]
    [InlineData(int.MaxValue, 480, 1, int.MaxValue - 1, 0, 2, 1)]
    [InlineData(640, int.MaxValue, 1, 0, int.MaxValue - 1, 1, 2)]
    public void InvalidGeometryAndOverflowingCropBoundsReturnNoPlacement(
        int videoWidth, int videoHeight, double pixelAspectRatio, int x, int y, int width, int height)
    {
        var geometry = new AssSubtitleGeometry(videoWidth, videoHeight, pixelAspectRatio, x, y, width, height);

        Assert.False(AssSubtitleLayoutPolicy.IsValid(geometry));
        Assert.False(AssSubtitleLayoutPolicy.TryPlace(1920, 1080, geometry, out var placement));
        Assert.Equal(default, placement);
    }

    [Fact]
    public void MaximumIntegerBoundsAcceptACropThatEndsExactlyAtTheEdge()
    {
        var geometry = new AssSubtitleGeometry(
            int.MaxValue, int.MaxValue, 1, int.MaxValue - 1, int.MaxValue - 1, 1, 1);

        Assert.True(AssSubtitleLayoutPolicy.IsValid(geometry));
    }

    [Fact]
    public void AViewportTooSmallToRepresentTheFittedVideoReturnsNoPlacement()
    {
        var geometry = new AssSubtitleGeometry(640, 480, 1, 0, 0, 640, 480);

        Assert.False(AssSubtitleLayoutPolicy.TryPlace(double.Epsilon, double.Epsilon, geometry, out var placement));
        Assert.Equal(default, placement);
    }

    [Theory]
    [InlineData(1428, 894, 786, 535.5, 734.953125, 357, 39.046875)]
    [InlineData(2560, 1440, 1330, 960, 1248, 640, 70)]
    public void OverlappingBottomBitmapClearsControlsWithoutScalingOrMovingTheVideo(
        double width, double height, double controlsTop,
        double expectedX, double expectedY, double expectedWidth, double expectedHeight)
    {
        var geometry = new AssSubtitleGeometry(1280, 720, 1, 480, 650, 320, 35);
        var original = Place(width, height, geometry);
        var controls = new AssSubtitleRectangle(44, controlsTop, width - 88, height - controlsTop - 22);

        var placement = AssSubtitleLayoutPolicy.AvoidBottomControls(original, controls, 12);

        Assert.Equal(original.Video, placement.Video);
        AssertRectangle(placement.Image, expectedX, expectedY, expectedWidth, expectedHeight);
        Assert.True(placement.Image.Y >= placement.Video.Y);
        Assert.Equal(12, controls.Y - placement.Image.Y - placement.Image.Height, precision: 9);
    }

    [Fact]
    public void NarrowLetterboxedVideoKeepsItsNonoverlappingBitmapAtTheAuthoredPosition()
    {
        var geometry = new AssSubtitleGeometry(1280, 720, 1, 480, 650, 320, 35);
        var original = Place(780, 894, geometry);
        var controls = new AssSubtitleRectangle(44, 784, 692, 88);

        var placement = AssSubtitleLayoutPolicy.AvoidBottomControls(original, controls, 12);

        Assert.Equal(original, placement);
        AssertRectangle(placement.Video, 0, 227.625, 780, 438.75);
        AssertRectangle(placement.Image, 292.5, 623.71875, 195, 21.328125);
    }

    [Fact]
    public void HiddenControlsRestoreTheOriginalBitmapAfterAnOverlappingLayout()
    {
        var geometry = new AssSubtitleGeometry(1280, 720, 1, 480, 650, 320, 35);
        var original = Place(1428, 894, geometry);
        var raised = AssSubtitleLayoutPolicy.AvoidBottomControls(original, new(44, 786, 1340, 86), 12);

        var restored = AssSubtitleLayoutPolicy.AvoidBottomControls(Place(1428, 894, geometry), null, 12);

        Assert.NotEqual(original.Image.Y, raised.Image.Y);
        Assert.Equal(original, restored);
    }

    [Theory]
    [InlineData(0, 700, 400, 100)]
    [InlineData(600, 700, 400, 100)]
    [InlineData(0, 760, 1000, 40)]
    [InlineData(0, 660, 1000, 40)]
    public void EdgeContactWithoutAnIntersectionDoesNotMoveTheBitmap(
        double x, double y, double width, double height)
    {
        var original = new AssSubtitlePlacement(new(0, 0, 1000, 800), new(400, 700, 200, 60));

        var placement = AssSubtitleLayoutPolicy.AvoidBottomControls(original, new(x, y, width, height), 12);

        Assert.Equal(original, placement);
    }

    [Fact]
    public void ATopBitmapDoesNotMoveWhenTheBottomControlsAreVisible()
    {
        var original = new AssSubtitlePlacement(new(240, 0, 1440, 1080), new(384, 216, 288, 108));

        var placement = AssSubtitleLayoutPolicy.AvoidBottomControls(original, new(44, 970, 1832, 88), 12);

        Assert.Equal(original, placement);
    }

    [Fact]
    public void ABitmapMayMoveExactlyToTheVideoTopWithoutEnteringTheLetterbox()
    {
        var original = new AssSubtitlePlacement(new(240, 140, 800, 600), new(340, 600, 200, 100));

        var placement = AssSubtitleLayoutPolicy.AvoidBottomControls(original, new(250, 252, 780, 488), 12);

        Assert.Equal(original.Video, placement.Video);
        AssertRectangle(placement.Image, 340, 140, 200, 100);
    }

    [Fact]
    public void ZeroClearanceMayPlaceTheBitmapExactlyAgainstTheControls()
    {
        var original = new AssSubtitlePlacement(new(0, 0, 1000, 800), new(400, 700, 200, 60));

        var placement = AssSubtitleLayoutPolicy.AvoidBottomControls(original, new(44, 720, 912, 58), 0);

        Assert.Equal(original.Video, placement.Video);
        AssertRectangle(placement.Image, 400, 660, 200, 60);
    }

    [Fact]
    public void InsufficientRoomAboveControlsPreservesTheBitmapInsteadOfClippingOrRescalingIt()
    {
        var original = new AssSubtitlePlacement(new(240, 140, 800, 600), new(340, 600, 200, 100));

        var placement = AssSubtitleLayoutPolicy.AvoidBottomControls(original, new(250, 250, 780, 490), 12);

        Assert.Equal(original, placement);
    }

    [Fact]
    public void AFullVideoBitmapRetainsItsGeometryWhenItCannotFitAboveTheControls()
    {
        var original = Place(1280, 720, new(1280, 720, 1, 0, 0, 1280, 720));

        var placement = AssSubtitleLayoutPolicy.AvoidBottomControls(original, new(44, 610, 1192, 88), 12);

        Assert.Equal(original, placement);
    }

    [Fact]
    public void NonSquarePixelsKeepTheirHorizontalScaleWhenABitmapNeedsVerticalClearance()
    {
        var original = Place(1024, 768, new(720, 576, 16d / 15d, 90, 500, 180, 40));

        var placement = AssSubtitleLayoutPolicy.AvoidBottomControls(original, new(32, 700, 960, 68), 12);

        Assert.Equal(original.Video, placement.Video);
        AssertRectangle(placement.Image, 128, 1904d / 3d, 256, 160d / 3d);
    }

    [Theory]
    [InlineData(double.NaN, 700, 1000, 100)]
    [InlineData(0, double.PositiveInfinity, 1000, 100)]
    [InlineData(0, 700, 0, 100)]
    [InlineData(0, 700, 1000, -1)]
    [InlineData(0, 700, double.PositiveInfinity, 100)]
    public void InvalidOrUnmeasuredControlsDoNotChangeTheBitmap(
        double x, double y, double width, double height)
    {
        var original = new AssSubtitlePlacement(new(0, 0, 1000, 800), new(400, 700, 200, 60));

        var placement = AssSubtitleLayoutPolicy.AvoidBottomControls(original, new(x, y, width, height), 12);

        Assert.Equal(original, placement);
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(-1)]
    public void InvalidClearanceDoesNotChangeTheBitmap(double clearance)
    {
        var original = new AssSubtitlePlacement(new(0, 0, 1000, 800), new(400, 700, 200, 60));

        var placement = AssSubtitleLayoutPolicy.AvoidBottomControls(original, new(44, 720, 912, 58), clearance);

        Assert.Equal(original, placement);
    }

    private static AssSubtitlePlacement Place(double width, double height, AssSubtitleGeometry geometry)
    {
        Assert.True(AssSubtitleLayoutPolicy.IsValid(geometry));
        Assert.True(AssSubtitleLayoutPolicy.TryPlace(width, height, geometry, out var placement));
        return placement;
    }

    private static void AssertRectangle(AssSubtitleRectangle rectangle, double x, double y, double width, double height)
    {
        Assert.Equal(x, rectangle.X, precision: 9);
        Assert.Equal(y, rectangle.Y, precision: 9);
        Assert.Equal(width, rectangle.Width, precision: 9);
        Assert.Equal(height, rectangle.Height, precision: 9);
    }
}
