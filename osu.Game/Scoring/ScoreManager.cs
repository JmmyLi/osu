// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Threading;
using System.Threading.Tasks;
using JetBrains.Annotations;
using osu.Framework.Bindables;
using osu.Framework.Extensions;
using osu.Framework.Platform;
using osu.Framework.Threading;
using osu.Game.Beatmaps;
using osu.Game.Configuration;
using osu.Game.Database;
using osu.Game.IO.Archives;
using osu.Game.Overlays.Notifications;
using osu.Game.Rulesets;
using osu.Game.Rulesets.Scoring;

namespace osu.Game.Scoring
{
    public class ScoreManager : IModelManager<ScoreInfo>, IModelImporter<ScoreInfo>
    {
        private readonly RealmAccess realm;
        private readonly Scheduler scheduler;
        private readonly Func<BeatmapDifficultyCache> difficulties;
        private readonly OsuConfigManager configManager;
        private readonly ScoreModelManager scoreModelManager;

        public ScoreManager(RulesetStore rulesets, Func<BeatmapManager> beatmaps, Storage storage, RealmAccess realm, Scheduler scheduler,
                            Func<BeatmapDifficultyCache> difficulties = null, OsuConfigManager configManager = null)
        {
            this.realm = realm;
            this.scheduler = scheduler;
            this.difficulties = difficulties;
            this.configManager = configManager;

            scoreModelManager = new ScoreModelManager(rulesets, beatmaps, storage, realm);
        }

        public Score GetScore(ScoreInfo score) => scoreModelManager.GetScore(score);

        /// <summary>
        /// Perform a lookup query on available <see cref="ScoreInfo"/>s.
        /// </summary>
        /// <param name="query">The query.</param>
        /// <returns>The first result for the provided query, or null if no results were found.</returns>
        public ScoreInfo Query(Expression<Func<ScoreInfo, bool>> query)
        {
            return realm.Run(r => r.All<ScoreInfo>().FirstOrDefault(query)?.Detach());
        }

        /// <summary>
        /// Orders an array of <see cref="ScoreInfo"/>s by total score.
        /// </summary>
        /// <param name="scores">The array of <see cref="ScoreInfo"/>s to reorder.</param>
        /// <param name="cancellationToken">A <see cref="CancellationToken"/> to cancel the process.</param>
        /// <returns>The given <paramref name="scores"/> ordered by decreasing total score.</returns>
        public async Task<ScoreInfo[]> OrderByTotalScoreAsync(ScoreInfo[] scores, CancellationToken cancellationToken = default)
        {
            var difficultyCache = difficulties?.Invoke();

            if (difficultyCache != null)
            {
                // Compute difficulties asynchronously first to prevent blocking via the GetTotalScore() call below.
                foreach (var s in scores)
                {
                    await difficultyCache.GetDifficultyAsync(s.BeatmapInfo, s.Ruleset, s.Mods, cancellationToken).ConfigureAwait(false);
                    cancellationToken.ThrowIfCancellationRequested();
                }
            }

            long[] totalScores = await Task.WhenAll(scores.Select(s => GetTotalScoreAsync(s, cancellationToken: cancellationToken))).ConfigureAwait(false);

            return scores.Select((score, index) => (score, totalScore: totalScores[index]))
                         .OrderByDescending(g => g.totalScore)
                         .ThenBy(g => g.score.OnlineID)
                         .Select(g => g.score)
                         .ToArray();
        }

        /// <summary>
        /// Retrieves a bindable that represents the total score of a <see cref="ScoreInfo"/>.
        /// </summary>
        /// <remarks>
        /// Responds to changes in the currently-selected <see cref="ScoringMode"/>.
        /// </remarks>
        /// <param name="score">The <see cref="ScoreInfo"/> to retrieve the bindable for.</param>
        /// <returns>The bindable containing the total score.</returns>
        public Bindable<long> GetBindableTotalScore([NotNull] ScoreInfo score) => new TotalScoreBindable(score, this, configManager);

        /// <summary>
        /// Retrieves a bindable that represents the formatted total score string of a <see cref="ScoreInfo"/>.
        /// </summary>
        /// <remarks>
        /// Responds to changes in the currently-selected <see cref="ScoringMode"/>.
        /// </remarks>
        /// <param name="score">The <see cref="ScoreInfo"/> to retrieve the bindable for.</param>
        /// <returns>The bindable containing the formatted total score string.</returns>
        public Bindable<string> GetBindableTotalScoreString([NotNull] ScoreInfo score) => new TotalScoreStringBindable(GetBindableTotalScore(score));

        /// <summary>
        /// Retrieves the total score of a <see cref="ScoreInfo"/> in the given <see cref="ScoringMode"/>.
        /// The score is returned in a callback that is run on the update thread.
        /// </summary>
        /// <param name="score">The <see cref="ScoreInfo"/> to calculate the total score of.</param>
        /// <param name="callback">The callback to be invoked with the total score.</param>
        /// <param name="mode">The <see cref="ScoringMode"/> to return the total score as.</param>
        /// <param name="cancellationToken">A <see cref="CancellationToken"/> to cancel the process.</param>
        public void GetTotalScore([NotNull] ScoreInfo score, [NotNull] Action<long> callback, ScoringMode mode = ScoringMode.Standardised, CancellationToken cancellationToken = default)
        {
            GetTotalScoreAsync(score, mode, cancellationToken)
                .ContinueWith(task => scheduler.Add(() =>
                {
                    if (!cancellationToken.IsCancellationRequested)
                        callback(task.GetResultSafely());
                }), TaskContinuationOptions.OnlyOnRanToCompletion);
        }

        /// <summary>
        /// Retrieves the total score of a <see cref="ScoreInfo"/> in the given <see cref="ScoringMode"/>.
        /// </summary>
        /// <param name="score">The <see cref="ScoreInfo"/> to calculate the total score of.</param>
        /// <param name="mode">The <see cref="ScoringMode"/> to return the total score as.</param>
        /// <param name="cancellationToken">A <see cref="CancellationToken"/> to cancel the process.</param>
        /// <returns>The total score.</returns>
        public async Task<long> GetTotalScoreAsync([NotNull] ScoreInfo score, ScoringMode mode = ScoringMode.Standardised, CancellationToken cancellationToken = default)
        {
            // TODO: This is required for playlist aggregate scores. They should likely not be getting here in the first place.
            if (string.IsNullOrEmpty(score.BeatmapInfo.MD5Hash))
                return score.TotalScore;

            int? beatmapMaxCombo = await GetMaximumAchievableComboAsync(score, cancellationToken).ConfigureAwait(false);
            if (beatmapMaxCombo == null)
                return score.TotalScore;

            if (beatmapMaxCombo == 0)
                return 0;

            var ruleset = score.Ruleset.CreateInstance();
            var scoreProcessor = ruleset.CreateScoreProcessor();
            scoreProcessor.Mods.Value = score.Mods;

            return (long)Math.Round(scoreProcessor.ComputeFinalLegacyScore(mode, score, beatmapMaxCombo.Value));
        }

        /// <summary>
        /// Retrieves the maximum achievable combo for the provided score.
        /// </summary>
        /// <param name="score">The <see cref="ScoreInfo"/> to compute the maximum achievable combo for.</param>
        /// <param name="cancellationToken">A <see cref="CancellationToken"/> to cancel the process.</param>
        /// <returns>The maximum achievable combo. A <see langword="null"/> return value indicates the difficulty cache has failed to retrieve the combo.</returns>
        public async Task<int?> GetMaximumAchievableComboAsync([NotNull] ScoreInfo score, CancellationToken cancellationToken = default)
        {
            if (score.IsLegacyScore)
            {
                // This score is guaranteed to be an osu!stable score.
                // The combo must be determined through either the beatmap's max combo value or the difficulty calculator, as lazer's scoring has changed and the score statistics cannot be used.
#pragma warning disable CS0618
                if (score.BeatmapInfo.MaxCombo != null)
                    return score.BeatmapInfo.MaxCombo.Value;
#pragma warning restore CS0618

                if (difficulties == null)
                    return null;

                // We can compute the max combo locally after the async beatmap difficulty computation.
                var difficulty = await difficulties().GetDifficultyAsync(score.BeatmapInfo, score.Ruleset, score.Mods, cancellationToken).ConfigureAwait(false);
                return difficulty?.MaxCombo;
            }

            // This is guaranteed to be a non-legacy score.
            // The combo must be determined through the score's statistics, as both the beatmap's max combo and the difficulty calculator will provide osu!stable combo values.
            return Enum.GetValues(typeof(HitResult)).OfType<HitResult>().Where(r => r.AffectsCombo()).Select(r => score.Statistics.GetValueOrDefault(r)).Sum();
        }

        /// <summary>
        /// Provides the total score of a <see cref="ScoreInfo"/>. Responds to changes in the currently-selected <see cref="ScoringMode"/>.
        /// </summary>
        private class TotalScoreBindable : Bindable<long>
        {
            private readonly Bindable<ScoringMode> scoringMode = new Bindable<ScoringMode>();
            private readonly ScoreInfo score;
            private readonly ScoreManager scoreManager;

            private CancellationTokenSource difficultyCalculationCancellationSource;

            /// <summary>
            /// Creates a new <see cref="TotalScoreBindable"/>.
            /// </summary>
            /// <param name="score">The <see cref="ScoreInfo"/> to provide the total score of.</param>
            /// <param name="scoreManager">The <see cref="ScoreManager"/>.</param>
            /// <param name="configManager">The config.</param>
            public TotalScoreBindable(ScoreInfo score, ScoreManager scoreManager, OsuConfigManager configManager)
            {
                this.score = score;
                this.scoreManager = scoreManager;

                configManager?.BindWith(OsuSetting.ScoreDisplayMode, scoringMode);
                scoringMode.BindValueChanged(onScoringModeChanged, true);
            }

            private void onScoringModeChanged(ValueChangedEvent<ScoringMode> mode)
            {
                difficultyCalculationCancellationSource?.Cancel();
                difficultyCalculationCancellationSource = new CancellationTokenSource();

                scoreManager.GetTotalScore(score, s => Value = s, mode.NewValue, difficultyCalculationCancellationSource.Token);
            }
        }

        /// <summary>
        /// Provides the total score of a <see cref="ScoreInfo"/> as a formatted string. Responds to changes in the currently-selected <see cref="ScoringMode"/>.
        /// </summary>
        private class TotalScoreStringBindable : Bindable<string>
        {
            // ReSharper disable once PrivateFieldCanBeConvertedToLocalVariable (need to hold a reference)
            private readonly IBindable<long> totalScore;

            public TotalScoreStringBindable(IBindable<long> totalScore)
            {
                this.totalScore = totalScore;
                this.totalScore.BindValueChanged(v => Value = v.NewValue.ToString("N0"), true);
            }
        }

        #region Implementation of IPostNotifications

        public Action<Notification> PostNotification
        {
            set => scoreModelManager.PostNotification = value;
        }

        #endregion

        #region Implementation of IModelManager<ScoreInfo>

        public bool Delete(ScoreInfo item)
        {
            return scoreModelManager.Delete(item);
        }

        public void Delete([CanBeNull] Expression<Func<ScoreInfo, bool>> filter = null, bool silent = false)
        {
            realm.Run(r =>
            {
                var items = r.All<ScoreInfo>()
                             .Where(s => !s.DeletePending);

                if (filter != null)
                    items = items.Where(filter);

                scoreModelManager.Delete(items.ToList(), silent);
            });
        }

        public void Delete(BeatmapInfo beatmap, bool silent = false)
        {
            realm.Run(r =>
            {
                var beatmapScores = r.Find<BeatmapInfo>(beatmap.ID).Scores.ToList();
                scoreModelManager.Delete(beatmapScores, silent);
            });
        }

        public void Delete(List<ScoreInfo> items, bool silent = false) => scoreModelManager.Delete(items, silent);

        public void Undelete(List<ScoreInfo> items, bool silent = false) => scoreModelManager.Undelete(items, silent);

        public void Undelete(ScoreInfo item) => scoreModelManager.Undelete(item);

        public Task Import(params string[] paths) => scoreModelManager.Import(paths);

        public Task Import(params ImportTask[] tasks) => scoreModelManager.Import(tasks);

        public IEnumerable<string> HandledExtensions => scoreModelManager.HandledExtensions;

        public Task<IEnumerable<Live<ScoreInfo>>> Import(ProgressNotification notification, params ImportTask[] tasks) => scoreModelManager.Import(notification, tasks);

        public Live<ScoreInfo> Import(ScoreInfo item, ArchiveReader archive = null, bool batchImport = false, CancellationToken cancellationToken = default) => scoreModelManager.Import(item, archive, batchImport, cancellationToken);

        public bool IsAvailableLocally(ScoreInfo model) => scoreModelManager.IsAvailableLocally(model);

        #endregion

        #region Implementation of IPresentImports<ScoreInfo>

        public Action<IEnumerable<Live<ScoreInfo>>> PostImport
        {
            set => scoreModelManager.PostImport = value;
        }

        #endregion
    }
}
