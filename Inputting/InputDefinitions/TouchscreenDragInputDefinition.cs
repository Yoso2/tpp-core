﻿namespace Inputting.InputDefinitions
{
    /// <summary>
    /// A touchscreen drag input is an input in the form of <c>x1,y1>x2,y2</c>, e.g. <c>120,215>80,170</c>,
    /// with <c>x</c> and <c>y</c> being within 0 (inclusive) and the specified max width/height (exclusive).
    /// The resulting input's effective text will be "drag".
    /// The dragged coordinates will be passed via additional data in the form of a 4-tuple <c>(x1, y1, x2, y2)</c>.
    /// </summary>
    public struct TouchscreenDragInputDefinition : IInputDefinition
    {
        public const string EffectiveText = "drag";

        private readonly int _width;
        private readonly int _height;

        public TouchscreenDragInputDefinition(int width, int height)
        {
            _width = width;
            _height = height;
        }

        public string InputRegex => @"\d{1,4},\d{1,4}>\d{1,4},\d{1,4}";

        public Input? Parse(string str)
        {
            var positions = str.Split(">", count: 2);
            var posFromSplit = positions[0].Split(",", count: 2);
            var posToSplit = positions[1].Split(",", count: 2);
            (int x1, int y1) = (int.Parse(posFromSplit[0]), int.Parse(posFromSplit[1]));
            (int x2, int y2) = (int.Parse(posToSplit[0]), int.Parse(posToSplit[1]));
            if (x1 >= _width || x2 >= _width || y1 >= _height || y2 >= _height)
            {
                return null;
            }
            return new Input(str, EffectiveText, str, additionalData: (x1, y1, x2, y2));
        }
    }
}