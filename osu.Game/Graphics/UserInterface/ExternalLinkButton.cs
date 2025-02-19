﻿// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using osu.Framework.Allocation;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Cursor;
using osu.Framework.Graphics.Sprites;
using osu.Framework.Input.Events;
using osu.Framework.Localisation;
using osu.Framework.Platform;
using osuTK;
using osuTK.Graphics;

namespace osu.Game.Graphics.UserInterface
{
    public class ExternalLinkButton : CompositeDrawable, IHasTooltip
    {
        public string Link { get; set; }

        private Color4 hoverColour;

        [Resolved]
        private GameHost host { get; set; }

        private readonly SpriteIcon linkIcon;

        public ExternalLinkButton(string link = null)
        {
            Link = link;
            Size = new Vector2(12);
            InternalChildren = new Drawable[]
            {
                linkIcon = new SpriteIcon
                {
                    Icon = FontAwesome.Solid.ExternalLinkAlt,
                    RelativeSizeAxes = Axes.Both
                },
                new HoverClickSounds()
            };
        }

        [BackgroundDependencyLoader]
        private void load(OsuColour colours)
        {
            hoverColour = colours.Yellow;
        }

        protected override bool OnHover(HoverEvent e)
        {
            linkIcon.FadeColour(hoverColour, 500, Easing.OutQuint);
            return base.OnHover(e);
        }

        protected override void OnHoverLost(HoverLostEvent e)
        {
            linkIcon.FadeColour(Color4.White, 500, Easing.OutQuint);
            base.OnHoverLost(e);
        }

        protected override bool OnClick(ClickEvent e)
        {
            if (Link != null)
                host.OpenUrlExternally(Link);
            return true;
        }

        public LocalisableString TooltipText => "view in browser";
    }
}
