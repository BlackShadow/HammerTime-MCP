using System;

namespace HammerTime.Mcp.Shared
{
    public static class TextureProjectionMath
    {
        public static double RegularPolygonPerimeter(int sides, double radius)
        {
            if (sides < 3) throw new ArgumentOutOfRangeException(nameof(sides), "A polygon must have at least 3 sides.");
            if (radius <= 0) throw new ArgumentOutOfRangeException(nameof(radius), "Radius must be positive.");

            return 2 * sides * radius * Math.Sin(Math.PI / sides);
        }

        public static double WrapScale(double perimeter, int textureWidth, int repeats)
        {
            if (perimeter <= 0) throw new ArgumentOutOfRangeException(nameof(perimeter), "Perimeter must be positive.");
            if (textureWidth <= 0) throw new ArgumentOutOfRangeException(nameof(textureWidth), "Texture width must be positive.");
            if (repeats <= 0) throw new ArgumentOutOfRangeException(nameof(repeats), "Repeat count must be positive.");

            return perimeter / (textureWidth * repeats);
        }
    }
}
