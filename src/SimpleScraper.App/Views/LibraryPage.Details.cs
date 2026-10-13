using SimpleScraper.Models;
using SimpleScraper.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Input;
using Windows.Storage.Pickers;
using System.Xml.Linq;

namespace SimpleScraper.Views;

public sealed partial class LibraryPage
{
    private async Task SelectMedia()
    {
        var version = ++_detailVersion; _details.Children.Clear(); _art.Children.Clear(); _cast.List.ItemsSource = null; UpdateActions(); if (Selected is not { } selected) { _details.Children.Add(Ui.Empty("Select a media item", "Select a movie, season or episode to view its metadata.")); return; }
        var m = selected.Media; var file = selected.File; var resolved = file == null ? null : EpisodeMatching.Resolve(m, file); var doc = file != null ? MetadataEditing.Episode(m, file) : selected.Season is >= 0 ? MetadataEditing.Season(m, selected.Season.Value) : m.Metadata; MetadataEpisode? episode = resolved?.Episode;
        if (_tabs.SelectedIndex == 0 && file?.NfoPath != null && episode == null)
        {
            _details.Children.Add(Text("Loading metadata…"));
            try { file.LocalMetadata ??= await Task.Run(() => MetadataEditing.ReadNfo(XDocument.Load(file.NfoPath).Root!, Path.GetDirectoryName(file.Path)!)); doc = MetadataEditing.Episode(m, file); }
            catch (Exception e) { if (version == _detailVersion) _status.Text = e.Message; }
            if (version != _detailVersion) return; _details.Children.Clear();
        }
        if (_tabs.SelectedIndex == 1)
        {
            if (doc != null && !doc.CreditsLoaded)
            {
                _status.Text = L.Text("Loading cast and production credits…");
                try
                {
                    var creditSource = resolved?.Document ?? doc;
                    await Task.Run(() => MetadataCredits.EnsureAsync(creditSource, m.Kind, _services.Tmdb(), _services.Bangumi()));
                    _store.Documents[EpisodeMatching.Key(creditSource, m.Kind)] = creditSource; await Save();
                    if (file != null) doc = MetadataEditing.Episode(m, file);
                }
                catch (Exception) { if (version == _detailVersion) _status.Text = L.Text("Cast and production credits could not be loaded. Existing matches are preserved."); }
                if (version != _detailVersion) return;
                if (doc.Warnings.Count > 0) _status.Text = string.Join("\n", doc.Warnings.Distinct()); else if (doc.CreditsLoaded) _status.Text = L.Text("Cast and production credits loaded.");
            }
            var people = new List<TableRow>();
            foreach (var actor in doc?.Cast ?? new()) people.Add(PersonRow(actor.Id, actor.Name, actor.Character, actor.ProfilePath));
            if (doc?.Crew.Count > 0)
            {
                people.Add(new() { Header = true, Cells = new() { new("", 64), new(L.Text("Production credits"), 220), new("", 260) } });
                foreach (var person in doc.Crew) people.Add(PersonRow(person.Id, person.Name, person.Job, person.ProfilePath));
            }
            _cast.List.ItemsSource = people; return;
            TableRow PersonRow(int id, string name, string role, string? profile)
            {
                var provider = doc?.Provider ?? "";
                var uri = _services.ActorImages().FindImage(provider, id, name, profile) ?? (profile != null && Path.IsPathRooted(profile) && File.Exists(profile) ? profile : ActorImageCache.RemoteUrl(provider, profile));
                return new() { Cells = new() { new("", 64) { Editor = () => new PersonPicture { DisplayName = name, Width = 44, Height = 44, IsHitTestVisible = false, ProfilePicture = string.IsNullOrEmpty(uri) ? null : new BitmapImage(new Uri(uri)) { DecodePixelWidth = 88 } } }, new(name, 220), new(role, 260) { Wrap = true } } };
            }
        }
        if (_tabs.SelectedIndex == 2) { var artwork = file != null ? file.ThumbPath == null ? new Dictionary<string, string>() : new() { [L.Text("Episode still")] = file.ThumbPath } : selected.Season is >= 0 ? m.SeasonArtworkPaths.GetValueOrDefault(selected.Season.Value) ?? new() : new Dictionary<string, string>(m.ArtworkPaths); if (file == null && selected.Season == null) foreach (var seasonArt in m.SeasonArtworkPaths) foreach (var art in seasonArt.Value) artwork[$"S{seasonArt.Key:00} · {art.Key}"] = art.Value; foreach (var art in artwork) { _art.Children.Add(Text(art.Key)); _art.Children.Add(Picture(art.Value, 320, 800)); } if (artwork.Count == 0) _art.Children.Add(Ui.Empty("No artwork available", "Scrape available images to display artwork here.")); Ui.Enter(_art); return; }
        var isExtra = file != null && LibraryReview.IsExtra(m, file);
        var title = Text(isExtra ? file!.Name : doc?.Title ?? m.Title, 22); title.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold; _details.Children.Add(title); var path = Text(file?.Path ?? m.Folder, 12); path.Style = Ui.Style("SecondaryTextStyle"); _details.Children.Add(path);
        if (file == null && m.ScanWarning.Length > 0) _details.Children.Add(new InfoBar { IsOpen = true, IsClosable = false, Severity = InfoBarSeverity.Warning, Title = L.Text("Existing NFO needs attention"), Message = m.ScanWarning });
        var picture = file != null ? file.ThumbPath ?? episode?.ImageUrl : selected.Season is >= 0 ? m.SeasonArtworkPaths.GetValueOrDefault(selected.Season.Value)?.GetValueOrDefault("poster") ?? doc?.Seasons.FirstOrDefault(s => s.Number == selected.Season)?.PosterUrl : m.PosterPath ?? doc?.PosterUrl;
        var facts = new StackPanel { Spacing = 10, VerticalAlignment = VerticalAlignment.Top };
        if (doc != null) { if (doc.OriginalTitle.Length > 0) facts.Children.Add(Text(L.Text("Original title") + ": " + doc.OriginalTitle)); facts.Children.Add(Text(doc.Date + (doc.Rating > 0 ? $" · ★ {doc.Rating:0.0}" : ""))); facts.Children.Add(Ui.SourceLinks(resolved?.Document ?? doc, m.Kind)); }
        if (!string.IsNullOrEmpty(picture))
        {
            if (file != null) { _details.Children.Add(Picture(picture, 210, 600)); _details.Children.Add(facts); }
            else { var summary = new Grid { ColumnSpacing = 16 }; summary.ColumnDefinitions.Add(new() { Width = new GridLength(150) }); summary.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) }); var poster = Picture(picture, 225, 320); poster.MaxWidth = 150; summary.Children.Add(poster); Grid.SetColumn(facts, 1); summary.Children.Add(facts); _details.Children.Add(summary); }
        }
        else _details.Children.Add(facts);
        if (isExtra) _details.Children.Add(Text("Extra video. Use Preview rename to organize it into extras; no episode match is required."));
        else if (file != null) _details.Children.Add(Text((file.NeedsReview ? file.SourceNumber.Length == 0 ? L.Text("Not assigned") : $"S{file.Season:00}E{file.SourceNumber}" : $"S{file.Season:00}E{file.Episode:00}") + $" · {episode?.Date}\n{file.Exclusion}"));
        if (file?.NeedsReview == true) _details.Children.Add(Text("Match the source episode first, then choose integer output numbering such as S00E01. Existing NFO numbering is not used automatically."));
        if (doc != null)
        {
            if (doc.Genres.Count > 0) _details.Children.Add(Text(L.Text("Genres") + ": " + string.Join(" / ", doc.Genres)));
            if (doc.Studios.Count > 0) _details.Children.Add(Text(L.Text("Studios") + ": " + string.Join(" / ", doc.Studios)));
            var directors = doc.Crew.Where(p => p.Job is "Director" or "导演" or "总导演" or "監督").Select(p => p.Name).Distinct().ToList();
            if (directors.Count > 0) _details.Children.Add(Text(L.Text("Directors") + ": " + string.Join(" / ", directors)));
            _details.Children.Add(Ui.Overview(doc.Overview, doc.OriginalOverview, file == null && selected.Season == null && doc.Provider == "TMDB" && !doc.OriginalOverviewLoaded ? async () =>
            {
                doc.OriginalOverview = await _services.Tmdb().GetOriginalOverviewAsync(int.Parse(doc.Id), m.Kind == LibraryMediaKind.Series);
                doc.OriginalOverviewLoaded = true; await Save(); return doc.OriginalOverview;
            } : null, doc.OverviewLanguage));
        }
        else _details.Children.Add(Text("Search and match metadata to continue."));
        Ui.Enter(_details);
    }

    private static Grid Picture(string uri, double height, int decode)
    {
        var grid = new Grid { MaxHeight = height, HorizontalAlignment = HorizontalAlignment.Left }; var loading = new ProgressRing { IsActive = true, Width = 28, Height = 28 }; grid.Children.Add(loading);
        var image = new Image { MaxHeight = height, Stretch = Stretch.Uniform }; var bitmap = new BitmapImage { DecodePixelWidth = decode }; bitmap.ImageOpened += (_, _) => { loading.IsActive = false; loading.Visibility = Visibility.Collapsed; Ui.Enter(image); }; bitmap.ImageFailed += (_, _) => { loading.IsActive = false; loading.Visibility = Visibility.Collapsed; }; bitmap.UriSource = new Uri(uri); image.Source = bitmap; grid.Children.Add(image); return grid;
    }
}
