using System.Runtime.InteropServices;
using HalconDotNet;
using OpenCvSharp;
using VisionWorkbench.Models;

namespace VisionWorkbench.Vision;

public static class ImageBridge
{
    public static PreviewFrame ToFrame(HObject image, IReadOnlyList<OverlayBox>? overlays = null)
    {
        HOperatorSet.GetImagePointer1(image, out HTuple pointer, out HTuple type, out HTuple width, out HTuple height);
        using (pointer) using (type) using (width) using (height)
        {
            byte[] pixels = new byte[width.I * height.I];
            Marshal.Copy(pointer.IP, pixels, 0, pixels.Length);
            return new(pixels, width.I, height.I, overlays ?? []);
        }
    }

    public static Mat ToMat(HObject image)
    {
        var frame = ToFrame(image);
        var mat = new Mat(frame.Height, frame.Width, MatType.CV_8UC1);
        Marshal.Copy(frame.Pixels, 0, mat.Data, frame.Pixels.Length);
        return mat;
    }

    public static HObject ToHalcon(Mat mat)
    {
        HOperatorSet.GenImage1(out HObject image, "byte", mat.Width, mat.Height, mat.Data);
        return image;
    }

    public static HObject CopyRoi(HObject original, HObject processed, RoiSpec roi, bool clearOutside = false)
    {
        using var target = ToMat(original);
        if (clearOutside) target.SetTo(Scalar.Black);
        using var source = ToMat(processed);
        using HObject region = CreateRoi(roi);
        HOperatorSet.RegionToBin(region, out HObject binary, 255, 0, target.Width, target.Height);
        using (binary)
        using (var mask = ToMat(binary)) source.CopyTo(target, mask);
        return ToHalcon(target);
    }

    public static HObject CreateRoi(RoiSpec roi)
    {
        HObject region;
        if (roi.Shape == "circle")
            HOperatorSet.GenCircle(out region, roi.Y + (roi.Height - 1) / 2,
                roi.X + (roi.Width - 1) / 2, (Math.Min(roi.Width, roi.Height) - 1) / 2);
        else
            HOperatorSet.GenRectangle1(out region, roi.Y, roi.X, roi.Y + roi.Height - 1, roi.X + roi.Width - 1);
        return region;
    }

    public static HObject ReadGray(string path)
    {
        HOperatorSet.ReadImage(out HObject original, path);
        using (original)
        {
            HOperatorSet.CountChannels(original, out HTuple channels);
            using (channels)
            {
                HObject gray;
                if (channels.I >= 3) HOperatorSet.Rgb1ToGray(original, out gray);
                else HOperatorSet.CopyImage(original, out gray);
                using (gray)
                {
                    HOperatorSet.GetImageType(gray, out HTuple type);
                    using (type)
                    {
                        if (type.S == "byte") { HOperatorSet.CopyImage(gray, out HObject result); return result; }
                        HOperatorSet.ScaleImageMax(gray, out HObject scaled);
                        using (scaled) { HOperatorSet.ConvertImageType(scaled, out HObject result, "byte"); return result; }
                    }
                }
            }
        }
    }
}
