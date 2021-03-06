// Copyright (c) 2007-2017 ppy Pty Ltd <contact@ppy.sh>.
// Licensed under the MIT Licence - https://raw.githubusercontent.com/ppy/osu/master/LICENCE

using System.Collections.Generic;
using osu.Framework.Allocation;
using osu.Framework.Extensions.Color4Extensions;
using System.IO;
using System.Threading.Tasks;
using OpenTK;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Shapes;
using osu.Framework.Graphics.Sprites;
using osu.Game.Beatmaps;
using osu.Game.Beatmaps.Drawables;
using osu.Game.Graphics;
using osu.Game.Graphics.Sprites;
using OpenTK.Graphics;
using osu.Framework.Input;
using osu.Game.Graphics.UserInterface;
using osu.Game.Online.API;
using osu.Framework.Logging;
using osu.Game.Beatmaps.IO;
using osu.Game.Overlays.Notifications;

namespace osu.Game.Overlays.Direct
{
    public abstract class DirectPanel : Container
    {
        public readonly BeatmapSetInfo SetInfo;

        protected Box BlackBackground;

        private const double hover_transition_time = 400;

        private Container content;

        private APIAccess api;
        private ProgressBar progressBar;
        private BeatmapManager beatmaps;
        private NotificationOverlay notifications;

        protected override Container<Drawable> Content => content;

        protected DirectPanel(BeatmapSetInfo setInfo)
        {
            SetInfo = setInfo;
        }

        private readonly EdgeEffectParameters edgeEffectNormal = new EdgeEffectParameters
        {
            Type = EdgeEffectType.Shadow,
            Offset = new Vector2(0f, 1f),
            Radius = 2f,
            Colour = Color4.Black.Opacity(0.25f),
        };

        private readonly EdgeEffectParameters edgeEffectHovered = new EdgeEffectParameters
        {
            Type = EdgeEffectType.Shadow,
            Offset = new Vector2(0f, 5f),
            Radius = 10f,
            Colour = Color4.Black.Opacity(0.3f),
        };


        [BackgroundDependencyLoader(permitNulls: true)]
        private void load(APIAccess api, BeatmapManager beatmaps, OsuColour colours, NotificationOverlay notifications)
        {
            this.api = api;
            this.beatmaps = beatmaps;
            this.notifications = notifications;

            AddInternal(content = new Container
            {
                RelativeSizeAxes = Axes.Both,
                Masking = true,
                EdgeEffect = edgeEffectNormal,
                Children = new[]
                {
                    // temporary blackness until the actual background loads.
                    BlackBackground = new Box
                    {
                        RelativeSizeAxes = Axes.Both,
                        Colour = Color4.Black,
                    },
                    CreateBackground(),
                    progressBar = new ProgressBar
                    {
                        Anchor = Anchor.BottomLeft,
                        Origin = Anchor.BottomLeft,
                        Height = 0,
                        Alpha = 0,
                        BackgroundColour = Color4.Black.Opacity(0.7f),
                        FillColour = colours.Blue,
                        Depth = -1,
                    },
                }
            });
        }

        protected override bool OnHover(InputState state)
        {
            content.TweenEdgeEffectTo(edgeEffectHovered, hover_transition_time, Easing.OutQuint);
            content.MoveToY(-4, hover_transition_time, Easing.OutQuint);

            return base.OnHover(state);
        }

        protected override void OnHoverLost(InputState state)
        {
            content.TweenEdgeEffectTo(edgeEffectNormal, hover_transition_time, Easing.OutQuint);
            content.MoveToY(0, hover_transition_time, Easing.OutQuint);

            base.OnHoverLost(state);
        }

        // this should eventually be moved to a more central place, like BeatmapManager.
        private DownloadBeatmapSetRequest downloadRequest;

        protected void StartDownload()
        {
            if (api == null) return;

            // we already have an active download running.
            if (downloadRequest != null)
            {
                content.MoveToX(-5, 50, Easing.OutSine).Then()
                       .MoveToX(5, 100, Easing.InOutSine).Then()
                       .MoveToX(-5, 100, Easing.InOutSine).Then()
                       .MoveToX(0, 50, Easing.InSine).Then();
                return;
            }

            if (!api.LocalUser.Value.IsSupporter)
            {
                notifications.Post(new SimpleNotification
                {
                    Icon = FontAwesome.fa_superpowers,
                    Text = "You gotta be a supporter to download for now 'yo"
                });
                return;
            }

            progressBar.FadeIn(400, Easing.OutQuint);
            progressBar.ResizeHeightTo(4, 400, Easing.OutQuint);

            progressBar.Current.Value = 0;

            ProgressNotification downloadNotification = new ProgressNotification
            {
                Text = $"Downloading {SetInfo.Metadata.Artist} - {SetInfo.Metadata.Title}",
            };

            downloadRequest = new DownloadBeatmapSetRequest(SetInfo);
            downloadRequest.Failure += e =>
            {
                progressBar.Current.Value = 0;
                progressBar.FadeOut(500);
                downloadNotification.State = ProgressNotificationState.Completed;
                Logger.Error(e, "Failed to get beatmap download information");

                downloadRequest = null;
            };

            downloadRequest.Progress += (current, total) =>
            {
                float progress = (float)current / total;

                progressBar.Current.Value = progress;

                downloadNotification.State = ProgressNotificationState.Active;
                downloadNotification.Progress = progress;
            };

            downloadRequest.Success += data =>
            {
                progressBar.Current.Value = 1;
                progressBar.FadeOut(500);

                downloadNotification.State = ProgressNotificationState.Completed;

                using (var stream = new MemoryStream(data))
                using (var archive = new OszArchiveReader(stream))
                    beatmaps.Import(archive);
            };

            downloadNotification.CancelRequested += () =>
            {
                downloadRequest.Cancel();
                downloadRequest = null;
                return true;
            };

            notifications.Post(downloadNotification);

            // don't run in the main api queue as this is a long-running task.
            Task.Run(() => downloadRequest.Perform(api));
        }

        public class DownloadBeatmapSetRequest : APIDownloadRequest
        {
            private readonly BeatmapSetInfo beatmapSet;

            public DownloadBeatmapSetRequest(BeatmapSetInfo beatmapSet)
            {
                this.beatmapSet = beatmapSet;
            }

            protected override string Target => $@"beatmapsets/{beatmapSet.OnlineBeatmapSetID}/download";
        }

        protected override void LoadComplete()
        {
            base.LoadComplete();
            this.FadeInFromZero(200, Easing.Out);
        }

        protected List<DifficultyIcon> GetDifficultyIcons()
        {
            var icons = new List<DifficultyIcon>();

            foreach (var b in SetInfo.Beatmaps)
                icons.Add(new DifficultyIcon(b));

            return icons;
        }

        protected Drawable CreateBackground() => new DelayedLoadWrapper(new BeatmapSetCover(SetInfo)
        {
            Anchor = Anchor.Centre,
            Origin = Anchor.Centre,
            RelativeSizeAxes = Axes.Both,
            FillMode = FillMode.Fill,
            OnLoadComplete = d =>
            {
                d.FadeInFromZero(400, Easing.Out);
                BlackBackground.Delay(400).FadeOut();
            },
        })
        {
            RelativeSizeAxes = Axes.Both,
            TimeBeforeLoad = 300
        };

        public class Statistic : FillFlowContainer
        {
            private readonly SpriteText text;

            private int value;

            public int Value
            {
                get { return value; }
                set
                {
                    this.value = value;
                    text.Text = Value.ToString(@"N0");
                }
            }

            public Statistic(FontAwesome icon, int value = 0)
            {
                Anchor = Anchor.TopRight;
                Origin = Anchor.TopRight;
                AutoSizeAxes = Axes.Both;
                Direction = FillDirection.Horizontal;
                Spacing = new Vector2(5f, 0f);

                Children = new Drawable[]
                {
                    text = new OsuSpriteText
                    {
                        Font = @"Exo2.0-SemiBoldItalic",
                    },
                    new SpriteIcon
                    {
                        Icon = icon,
                        Shadow = true,
                        Size = new Vector2(14),
                        Margin = new MarginPadding { Top = 1 },
                    },
                };

                Value = value;
            }
        }
    }
}
