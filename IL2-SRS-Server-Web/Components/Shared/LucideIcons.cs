using System.Collections.Generic;

namespace Ciribob.IL2.SimpleRadio.Standalone.Server.Components.Shared
{
    /// <summary>
    /// SVG contents of the Lucide icons used by the admin UI (https://lucide.dev, lucide-static 1.50.0,
    /// ISC License, Copyright (c) Lucide Icons and Contributors). Bundled so the UI works without internet access.
    /// To add an icon, copy the inner elements of icons/&lt;name&gt;.svg from the lucide-static package.
    /// </summary>
    internal static class LucideIcons
    {
        public static readonly IReadOnlyDictionary<string, string> Paths = new Dictionary<string, string>
        {
            ["monitor"] = "<rect width=\"20\" height=\"14\" x=\"2\" y=\"3\" rx=\"2\" /> <line x1=\"8\" x2=\"16\" y1=\"21\" y2=\"21\" /> <line x1=\"12\" x2=\"12\" y1=\"17\" y2=\"21\" />",
            ["sun"] = "<circle cx=\"12\" cy=\"12\" r=\"4\" /> <path d=\"M12 2v2\" /> <path d=\"M12 20v2\" /> <path d=\"m4.93 4.93 1.41 1.41\" /> <path d=\"m17.66 17.66 1.41 1.41\" /> <path d=\"M2 12h2\" /> <path d=\"M20 12h2\" /> <path d=\"m6.34 17.66-1.41 1.41\" /> <path d=\"m19.07 4.93-1.41 1.41\" />",
            ["moon"] = "<path d=\"M20.985 12.486a9 9 0 1 1-9.473-9.472c.405-.022.617.46.402.803a6 6 0 0 0 8.268 8.268c.344-.215.825-.004.803.401\" />",
        };
    }
}
