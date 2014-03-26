//
// This is the view engine from Aurora
//
// Frank Hale <frankhale@gmail.com>
// 31 December 2013
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.
//
// This program is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
// GNU General Public License for more details.
//
// You should have received a copy of the GNU General Public License
// along with this program.  If not, see <http://www.gnu.org/licenses/>.
//

using MarkdownSharp;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Web;
using System.Xml.Linq;

namespace ViewEngine
{
	#region INTERFACES AND ENUMS
	// This determines at what point the view compiler runs the particular 
	// transformation on the template.
	internal enum DirectiveProcessType { Compile, AfterCompile, Render }

	internal interface IViewCompiler
	{
		List<TemplateInfo> CompileAll();
		TemplateInfo Compile(string fullName);
		TemplateInfo Render(string fullName, Dictionary<string, string> tags);
	}

	public interface IViewEngine
	{
		string LoadView(string fullName, Dictionary<string, string> tags);
		string GetCache();
		bool CacheUpdated { get; }
	}
	#endregion

	#region DIRECTIVES AND SUBSTITUTIONS
	internal interface IViewCompilerDirectiveHandler
	{
		DirectiveProcessType Type { get; }
		StringBuilder Process(ViewCompilerDirectiveInfo directiveInfo);
	}

	internal interface IViewCompilerSubstitutionHandler
	{
		DirectiveProcessType Type { get; }
		StringBuilder Process(StringBuilder content);
	}

	internal class HeadSubstitution : IViewCompilerSubstitutionHandler
	{
		private static readonly Regex HeadBlockRe = new Regex(@"\[\[(?<block>[\s\S]+?)\]\]", RegexOptions.Compiled);
		private const string HeadDirective = "%%Head%%";

		public DirectiveProcessType Type { get; private set; }

		public HeadSubstitution()
		{
			Type = DirectiveProcessType.Compile;
		}

		public StringBuilder Process(StringBuilder content)
		{
			MatchCollection heads = HeadBlockRe.Matches(content.ToString());

			if (heads.Count > 0)
			{
				var headSubstitutions = new StringBuilder();

				foreach (Match head in heads)
				{
					headSubstitutions.Append(Regex.Replace(head.Groups["block"].Value, @"^(\s+)", string.Empty, RegexOptions.Multiline));
					content.Replace(head.Value, string.Empty);
				}

				content.Replace(HeadDirective, headSubstitutions.ToString());
			}

			content.Replace(HeadDirective, string.Empty);

			return content;
		}
	}

	internal class AntiForgeryTokenSubstitution : IViewCompilerSubstitutionHandler
	{
		private const string TokenName = "%%AntiForgeryToken%%";
		private readonly Func<string> _createAntiForgeryToken;

		public DirectiveProcessType Type { get; private set; }

		public AntiForgeryTokenSubstitution(Func<string> createAntiForgeryToken)
		{
			this._createAntiForgeryToken = createAntiForgeryToken;

			Type = DirectiveProcessType.Render;
		}

		public StringBuilder Process(StringBuilder content)
		{
			var tokens = Regex.Matches(content.ToString(), TokenName)
												.Cast<Match>()
												.Select(m => new { Start = m.Index, End = m.Length })
												.Reverse();

			foreach (var t in tokens)
				content.Replace(TokenName, _createAntiForgeryToken(), t.Start, t.End);

			return content;
		}
	}

	internal class CommentSubstitution : IViewCompilerSubstitutionHandler
	{
		private static readonly Regex CommentBlockRe = new Regex(@"\@\@(?<block>[\s\S]+?)\@\@", RegexOptions.Compiled);

		public DirectiveProcessType Type { get; private set; }

		public CommentSubstitution()
		{
			Type = DirectiveProcessType.Compile;
		}

		public StringBuilder Process(StringBuilder content)
		{
			return new StringBuilder(CommentBlockRe.Replace(content.ToString(), string.Empty));
		}
	}

	internal class MasterPageDirective : IViewCompilerDirectiveHandler
	{
		private const string TokenName = "%%View%%";
		public DirectiveProcessType Type { get; private set; }

		public MasterPageDirective()
		{
			Type = DirectiveProcessType.Compile;
		}

		public StringBuilder Process(ViewCompilerDirectiveInfo directiveInfo)
		{
			if (directiveInfo.Directive != "Master") return directiveInfo.Content;

			var finalPage = new StringBuilder();

			var masterPageName = directiveInfo.DetermineKeyName(directiveInfo.Value);
			var masterPageTemplate = directiveInfo.ViewTemplates
				.FirstOrDefault(x => x.FullName == masterPageName)
				.Template;

			directiveInfo.AddPageDependency(masterPageName);

			finalPage.Append(masterPageTemplate);
			finalPage.Replace(TokenName, directiveInfo.Content.ToString());
			finalPage.Replace(directiveInfo.Match.Groups[0].Value, string.Empty);

			return finalPage;
		}
	}

	internal class PartialPageDirective : IViewCompilerDirectiveHandler
	{
		public DirectiveProcessType Type { get; private set; }

		public PartialPageDirective()
		{
			Type = DirectiveProcessType.AfterCompile;
		}

		public StringBuilder Process(ViewCompilerDirectiveInfo directiveInfo)
		{
			if (directiveInfo.Directive != "Partial") return directiveInfo.Content;

			var partialPageName = directiveInfo.DetermineKeyName(directiveInfo.Value);
			var partialPageTemplate = directiveInfo.ViewTemplates
				.FirstOrDefault(x => x.FullName == partialPageName)
				.Template;

			directiveInfo.Content.Replace(directiveInfo.Match.Groups[0].Value, partialPageTemplate);

			return directiveInfo.Content;
		}
	}

	internal class HelperBundleDirective : IViewCompilerSubstitutionHandler
	{
		public DirectiveProcessType Type { get; private set; }
		private const string HelperBundlesDirective = "%%HelperBundles%%";
		private readonly Func<Dictionary<string, StringBuilder>> _getHelperBundles;
		private readonly string _sharedResourceFolderPath;
		private const string CssIncludeTag = "<link href=\"{0}\" rel=\"stylesheet\" type=\"text/css\" />";
		private const string JsIncludeTag = "<script src=\"{0}\" type=\"text/javascript\"></script>";

		public HelperBundleDirective(string sharedResourceFolderPath, Func<Dictionary<string, StringBuilder>> getHelperBundles)
		{
			Type = DirectiveProcessType.Render;
			this._getHelperBundles = getHelperBundles;
			this._sharedResourceFolderPath = sharedResourceFolderPath;
		}

		public string ProcessBundleLink(string bundlePath)
		{
			var tag = string.Empty;
			var extension = Path.GetExtension(bundlePath).Substring(1).ToLower();
			var isAPath = bundlePath.Contains('/') ? true : false;
			var modifiedBundlePath = bundlePath;

			if (!isAPath)
				modifiedBundlePath = string.Join("/", _sharedResourceFolderPath, extension, bundlePath);

			switch (extension)
			{
				case "css":
					tag = string.Format(CssIncludeTag, modifiedBundlePath);
					break;
				case "js":
					tag = string.Format(JsIncludeTag, modifiedBundlePath);
					break;
			}

			return tag;
		}

		public StringBuilder Process(StringBuilder content)
		{
			if (!content.ToString().Contains(HelperBundlesDirective)) return content;

			var fileLinkBuilder = new StringBuilder();

			foreach (var bundlePath in _getHelperBundles().Keys)
				fileLinkBuilder.AppendLine(ProcessBundleLink(bundlePath));

			content.Replace(HelperBundlesDirective, fileLinkBuilder.ToString());

			return content;
		}
	}

	internal class BundleDirective : IViewCompilerDirectiveHandler
	{
		private readonly bool _debugMode;
		private readonly string _sharedResourceFolderPath;
		private readonly Func<string, string[]> _getBundleFiles;
		private readonly Dictionary<string, string> _bundleLinkResults;
		private const string CssIncludeTag = "<link href=\"{0}\" rel=\"stylesheet\" type=\"text/css\" />";
		private const string JsIncludeTag = "<script src=\"{0}\" type=\"text/javascript\"></script>";

		public DirectiveProcessType Type { get; private set; }

		public BundleDirective(bool debugMode, string sharedResourceFolderPath,
			Func<string, string[]> getBundleFiles)
		{
			this._debugMode = debugMode;
			this._sharedResourceFolderPath = sharedResourceFolderPath;
			this._getBundleFiles = getBundleFiles;

			_bundleLinkResults = new Dictionary<string, string>();

			Type = DirectiveProcessType.Render;
		}

		public string ProcessBundleLink(string bundlePath)
		{
			var tag = string.Empty;
			var extension = Path.GetExtension(bundlePath).Substring(1).ToLower();
			var isAPath = bundlePath.Contains('/') ? true : false;
			var modifiedBundlePath = bundlePath;

			if (!isAPath)
				modifiedBundlePath = string.Join("/", _sharedResourceFolderPath, extension, bundlePath);

			switch (extension)
			{
				case "css":
					tag = string.Format(CssIncludeTag, modifiedBundlePath);
					break;
				case "js":
					tag = string.Format(JsIncludeTag, modifiedBundlePath);
					break;
			}

			return tag;
		}

		public StringBuilder Process(ViewCompilerDirectiveInfo directiveInfo)
		{
			var bundleName = directiveInfo.Value;

			switch (directiveInfo.Directive)
			{
				case "Include":
					directiveInfo.Content.Replace(directiveInfo.Match.Groups[0].Value, ProcessBundleLink(bundleName));
					break;
				case "Bundle":
					{
						var fileLinkBuilder = new StringBuilder();

						if (_bundleLinkResults.ContainsKey(bundleName))
						{
							fileLinkBuilder.AppendLine(_bundleLinkResults[bundleName]);
						}
						else
						{
							if (!string.IsNullOrEmpty(bundleName))
							{
								if (_debugMode)
								{
									var bundles = _getBundleFiles(bundleName);

									if (bundles != null)
									{
										foreach (string bundlePath in _getBundleFiles(bundleName))
											fileLinkBuilder.AppendLine(ProcessBundleLink(bundlePath));
									}
								}
								else
									fileLinkBuilder.AppendLine(ProcessBundleLink(bundleName));
							}

							_bundleLinkResults[bundleName] = fileLinkBuilder.ToString();
						}

						directiveInfo.Content.Replace(directiveInfo.Match.Groups[0].Value, fileLinkBuilder.ToString());
					}
					break;
			}

			return directiveInfo.Content;
		}
	}

	internal class PlaceHolderDirective : IViewCompilerDirectiveHandler
	{
		public DirectiveProcessType Type { get; private set; }

		public PlaceHolderDirective()
		{
			Type = DirectiveProcessType.AfterCompile;
		}

		public StringBuilder Process(ViewCompilerDirectiveInfo directiveInfo)
		{
			if (directiveInfo.Directive != "Placeholder") return directiveInfo.Content;

			var placeholderMatch = (new Regex(string.Format(@"\[{0}\](?<block>[\s\S]+?)\[/{0}\]", directiveInfo.Value)))
				.Match(directiveInfo.Content.ToString());

			if (!placeholderMatch.Success) return directiveInfo.Content;

			directiveInfo.Content.Replace(directiveInfo.Match.Groups[0].Value, placeholderMatch.Groups["block"].Value);
			directiveInfo.Content.Replace(placeholderMatch.Groups[0].Value, string.Empty);

			return directiveInfo.Content;
		}
	}
	#endregion

	#region VIEW ENGINE INTERNALS
	internal class ViewCache
	{
		public List<TemplateInfo> ViewTemplates;
		public List<TemplateInfo> CompiledViews;
		public Dictionary<string, List<string>> ViewDependencies;
	}

	internal class TemplateInfo
	{
		public string Name { get; set; }
		public string FullName { get; set; }
		public string Path { get; set; }
		public string Template { get; set; }
		public string TemplateMd5Sum { get; set; }
		public string Result { get; set; }
	}

	internal class TemplateLoader
	{
		private readonly string _appRoot;
		private readonly string[] _viewRoots;

		public TemplateLoader(string appRoot,
													string[] viewRoots)
		{
			appRoot.ThrowIfArgumentNull();

			this._appRoot = appRoot;
			this._viewRoots = viewRoots;
		}

		public List<TemplateInfo> Load()
		{
			var templates = new List<TemplateInfo>();

			foreach (var path in _viewRoots.Select(viewRoot => Path.Combine(_appRoot, viewRoot)).Where(Directory.Exists))
			{
				templates.AddRange(new DirectoryInfo(path).GetFiles("*.html", SearchOption.AllDirectories).Select(fi => Load(fi.FullName)));
			}

			return templates;
		}

		public TemplateInfo Load(string path)
		{
			var viewRoot = _viewRoots.FirstOrDefault(x => path.StartsWith(Path.Combine(_appRoot, x)));

			if (string.IsNullOrEmpty(viewRoot)) return null;

			var rootDir = new DirectoryInfo(viewRoot);

			var extension = Path.GetExtension(path);
			var templateName = Path.GetFileNameWithoutExtension(path);
			var templateKeyName = path.Replace(rootDir.Parent.FullName, string.Empty)
																	 .Replace(_appRoot, string.Empty)
																	 .Replace(extension, string.Empty)
																	 .Replace("\\", "/").TrimStart('/');
			var template = File.ReadAllText(path);

			return new TemplateInfo()
			{
				TemplateMd5Sum = template.CalculateMD5sum(),
				FullName = templateKeyName,
				Name = templateName,
				Path = path,
				Template = template
			};
		}
	}

	internal class ViewCompilerDirectiveInfo
	{
		public Match Match { get; set; }
		public string Directive { get; set; }
		public string Value { get; set; }
		public StringBuilder Content { get; set; }
		public List<TemplateInfo> ViewTemplates { get; set; }
		public Func<string, string> DetermineKeyName { get; set; }
		public Action<string> AddPageDependency { get; set; }
	}

	// This view engine is a simple tag based engine with master pages, partial views and Html fragments.
	// The compiler works by executing a number of directive and substitution handlers to transform
	// the Html templates. All templates are compiled and cached.
	internal class ViewCompiler : IViewCompiler
	{
		private readonly List<IViewCompilerDirectiveHandler> _directiveHandlers;
		private readonly List<IViewCompilerSubstitutionHandler> _substitutionHandlers;

		private readonly List<TemplateInfo> _viewTemplates;
		private readonly List<TemplateInfo> _compiledViews;
		private readonly Dictionary<string, List<string>> _viewDependencies;
		//private Dictionary<string, HashSet<string>> _templateKeyNames;

		private static readonly Regex DirectiveTokenRe = new Regex(@"(\%\%(?<directive>[a-zA-Z0-9]+)=(?<value>(\S|\.)+)\%\%)", RegexOptions.Compiled);
		private static readonly Regex TagRe = new Regex(@"{({|\||\!)([\w]+)(}|\!|\|)}", RegexOptions.Compiled);
		private const string TagFormatPattern = @"({{({{|\||\!){0}(\||\!|}})}})";
		private const string TagEncodingHint = "{|";
		private const string MarkdownEncodingHint = "{!";
		private const string UnencodedTagHint = "{{";

		private readonly StringBuilder _directive = new StringBuilder();
		private readonly StringBuilder _value = new StringBuilder();

		public ViewCompiler(List<TemplateInfo> viewTemplates,
												List<TemplateInfo> compiledViews,
												Dictionary<string, List<string>> viewDependencies,
												List<IViewCompilerDirectiveHandler> directiveHandlers,
												List<IViewCompilerSubstitutionHandler> substitutionHandlers)
		{
			viewTemplates.ThrowIfArgumentNull();
			compiledViews.ThrowIfArgumentNull();
			viewDependencies.ThrowIfArgumentNull();
			directiveHandlers.ThrowIfArgumentNull();
			substitutionHandlers.ThrowIfArgumentNull();

			this._viewTemplates = viewTemplates;
			this._compiledViews = compiledViews;
			this._viewDependencies = viewDependencies;
			this._directiveHandlers = directiveHandlers;
			this._substitutionHandlers = substitutionHandlers;

			//_templateKeyNames = new Dictionary<string, HashSet<string>>();
		}

		public List<TemplateInfo> CompileAll()
		{
			foreach (TemplateInfo vt in _viewTemplates)
			{
				if (!vt.FullName.Contains("Fragment"))
					Compile(vt.FullName);
				else
				{
					_compiledViews.Add(new TemplateInfo()
					{
						FullName = vt.FullName,
						Name = vt.Name,
						Template = vt.Template,
						Result = string.Empty,
						TemplateMd5Sum = vt.TemplateMd5Sum,
						Path = vt.Path
					});
				}
			}

			return _compiledViews;
		}

		public TemplateInfo Compile(string fullName)
		{
			TemplateInfo viewTemplate = _viewTemplates.FirstOrDefault(x => x.FullName == fullName);

			if (viewTemplate != null)
			{
				var rawView = new StringBuilder(viewTemplate.Template);
				var compiledView = new StringBuilder();

				if (!viewTemplate.FullName.Contains("Fragment"))
					compiledView = ProcessDirectives(fullName, rawView);

				if (string.IsNullOrEmpty(compiledView.ToString()))
					compiledView = rawView;

				compiledView.Replace(compiledView.ToString(), Regex.Replace(compiledView.ToString(), @"^\s*$\n", string.Empty, RegexOptions.Multiline));

				var view = new TemplateInfo()
				{
					FullName = fullName,
					Name = viewTemplate.Name,
					Template = compiledView.ToString(),
					Result = string.Empty,
					TemplateMd5Sum = viewTemplate.TemplateMd5Sum
				};

				var previouslyCompiled = _compiledViews.FirstOrDefault(x => x.FullName == viewTemplate.FullName);

				if (previouslyCompiled != null)
					_compiledViews.Remove(previouslyCompiled);

				_compiledViews.Add(view);

				return view;
			}

			throw new FileNotFoundException(string.Format("Cannot find view : {0}", fullName));
		}

		public TemplateInfo Render(string fullName, Dictionary<string, string> tags)
		{
			var compiledView = _compiledViews.FirstOrDefault(x => x.FullName == fullName);

			if (compiledView != null)
			{
				var compiledViewSb = new StringBuilder(compiledView.Template);

				compiledViewSb = _substitutionHandlers.Where(x => x.Type == DirectiveProcessType.Render)
																							.Aggregate(compiledViewSb, (current, sub) => sub.Process(current));

				foreach (var dir in _directiveHandlers.Where(x => x.Type == DirectiveProcessType.Render))
				{
					var dirMatches = DirectiveTokenRe.Matches(compiledViewSb.ToString());

					foreach (Match match in dirMatches)
					{
						_directive.Clear();
						_directive.Insert(0, match.Groups["directive"].Value);

						_value.Clear();
						_value.Insert(0, match.Groups["value"].Value);

						compiledViewSb = dir.Process(new ViewCompilerDirectiveInfo()
						{
							Match = match,
							Directive = _directive.ToString(),
							Value = _value.ToString(),
							Content = compiledViewSb,
							ViewTemplates = _viewTemplates,
							AddPageDependency = null, // This is in the pipeline to be fixed
							DetermineKeyName = null // This is in the pipeline to be fixed
						});
					}
				}

				if (tags != null)
				{
					var tagSb = new StringBuilder();

					foreach (var tag in tags)
					{
						tagSb.Clear();
						tagSb.Insert(0, string.Format(TagFormatPattern, tag.Key));

						var tempTagRe = new Regex(tagSb.ToString());
						var tagMatches = tempTagRe.Matches(compiledViewSb.ToString());

						foreach (Match m in tagMatches)
						{
							if (string.IsNullOrEmpty(tag.Value)) continue;

							if (m.Value.StartsWith(UnencodedTagHint))
								compiledViewSb.Replace(m.Value, tag.Value.Trim());
							else if (m.Value.StartsWith(TagEncodingHint))
								compiledViewSb.Replace(m.Value, HttpUtility.HtmlEncode(tag.Value.Trim()));
							else if (m.Value.StartsWith(MarkdownEncodingHint))
								compiledViewSb.Replace(m.Value, new Markdown().Transform((tag.Value.Trim())));
						}
					}

					var leftoverMatches = TagRe.Matches(compiledViewSb.ToString());

					foreach (Match match in leftoverMatches)
						compiledViewSb.Replace(match.Value, string.Empty);
				}

				compiledView.Result = compiledViewSb.ToString();

				return compiledView;
			}

			return null;
		}

		public StringBuilder ProcessDirectives(string fullViewName, StringBuilder rawView)
		{
			StringBuilder pageContent = new StringBuilder(rawView.ToString());

			if (!_viewDependencies.ContainsKey(fullViewName))
				_viewDependencies[fullViewName] = new List<string>();

			#region CLOSURES
			Action<string> addPageDependency = x =>
			{
				if (!_viewDependencies[fullViewName].Contains(x))
					_viewDependencies[fullViewName].Add(x);
			};

			Func<string, string> determineKeyName = name =>
			{
				return _viewTemplates.Select(y => y.FullName)
														.Where(z => z.Contains("Shared/" + name))
														.FirstOrDefault();
			};

			Action<IEnumerable<IViewCompilerDirectiveHandler>> performCompilerPass = x =>
			{
				MatchCollection dirMatches = DirectiveTokenRe.Matches(pageContent.ToString());

				foreach (Match match in dirMatches)
				{
					_directive.Clear();
					_directive.Insert(0, match.Groups["directive"].Value);

					_value.Clear();
					_value.Insert(0, match.Groups["value"].Value);

					foreach (IViewCompilerDirectiveHandler handler in x)
					{
						pageContent.Replace(pageContent.ToString(),
								handler.Process(new ViewCompilerDirectiveInfo()
								{
									Match = match,
									Directive = _directive.ToString(),
									Value = _value.ToString(),
									Content = pageContent,
									ViewTemplates = _viewTemplates,
									DetermineKeyName = determineKeyName,
									AddPageDependency = addPageDependency
								}).ToString());
					}
				}
			};
			#endregion

			performCompilerPass(_directiveHandlers.Where(x => x.Type == DirectiveProcessType.Compile));

			foreach (IViewCompilerSubstitutionHandler sub in _substitutionHandlers.Where(x => x.Type == DirectiveProcessType.Compile))
				pageContent = sub.Process(pageContent);

			performCompilerPass(_directiveHandlers.Where(x => x.Type == DirectiveProcessType.AfterCompile));

			return pageContent;
		}

		public void RecompileDependencies(string fullViewName)
		{
			var deps = _viewDependencies.Where(x => x.Value.FirstOrDefault(y => y == fullViewName) != null);

			Action<string> compile = name =>
			{
				var template = _viewTemplates.FirstOrDefault(x => x.FullName == name);

				if (template != null)
					Compile(template.FullName);
			};

			if (deps.Count() > 0)
			{
				foreach (KeyValuePair<string, List<string>> view in deps)
					compile(view.Key);
			}
			else
				compile(fullViewName);
		}
	}

	internal class ViewEngine : IViewEngine
	{
		private string appRoot;
		private List<IViewCompilerDirectiveHandler> dirHandlers;
		private List<IViewCompilerSubstitutionHandler> substitutionHandlers;
		private List<TemplateInfo> viewTemplates;
		private List<TemplateInfo> compiledViews;
		private Dictionary<string, List<string>> viewDependencies;
		private TemplateLoader viewTemplateLoader;
		private ViewCompiler viewCompiler;

		public bool CacheUpdated { get; private set; }

		public ViewEngine(string appRoot,
											string[] viewRoots,
											List<IViewCompilerDirectiveHandler> dirHandlers,
											List<IViewCompilerSubstitutionHandler> substitutionHandlers,
											string cache)
		{
			this.appRoot = appRoot;

			this.dirHandlers = dirHandlers;
			this.substitutionHandlers = substitutionHandlers;

			viewTemplateLoader = new TemplateLoader(appRoot, viewRoots);

			FileSystemWatcher watcher = new FileSystemWatcher(appRoot, "*.html");

			watcher.NotifyFilter = NotifyFilters.LastWrite;
			watcher.Changed += new FileSystemEventHandler(OnChanged);
			watcher.IncludeSubdirectories = true;
			watcher.EnableRaisingEvents = true;

			if (!(viewRoots.Count() >= 1))
				throw new ArgumentException("At least one view root is required to load view templates from.");

			ViewCache viewCache = null;

			if (!string.IsNullOrEmpty(cache))
			{
				viewCache = JsonConvert.DeserializeObject<ViewCache>(cache);

				if (viewCache != null)
				{
					viewTemplates = viewCache.ViewTemplates;
					compiledViews = viewCache.CompiledViews;
					viewDependencies = viewCache.ViewDependencies;
				}
			}
			else
			{
				compiledViews = new List<TemplateInfo>();
				viewDependencies = new Dictionary<string, List<string>>();
				viewTemplates = viewTemplateLoader.Load();
			}

			viewCompiler = new ViewCompiler(viewTemplates, compiledViews, viewDependencies, dirHandlers, substitutionHandlers);

			if (!(compiledViews.Count() > 0))
				compiledViews = viewCompiler.CompileAll();
		}

		private void OnChanged(object sender, FileSystemEventArgs e)
		{
			FileSystemWatcher fsw = sender as FileSystemWatcher;

			try
			{
				fsw.EnableRaisingEvents = false;

				while (CanOpenForRead(e.FullPath) == false)
					Thread.Sleep(1000);

				var changedTemplate = viewTemplateLoader.Load(e.FullPath);
				viewTemplates.Remove(viewTemplates.Find(x => x.FullName == changedTemplate.FullName));
				viewTemplates.Add(changedTemplate);

				var cv = compiledViews.FirstOrDefault(x => x.FullName == changedTemplate.FullName && x.TemplateMd5Sum != changedTemplate.TemplateMd5Sum);

				if (cv != null && !changedTemplate.FullName.Contains("Fragment"))
				{
					cv.TemplateMd5Sum = changedTemplate.TemplateMd5Sum;
					cv.Template = changedTemplate.Template;
					cv.Result = string.Empty;
				}

				viewCompiler = new ViewCompiler(viewTemplates, compiledViews, viewDependencies, dirHandlers, substitutionHandlers);

				if (cv != null)
					viewCompiler.RecompileDependencies(changedTemplate.FullName);
				else
					viewCompiler.Compile(changedTemplate.FullName);

				CacheUpdated = true;
			}
			finally
			{
				fsw.EnableRaisingEvents = true;
			}
		}

		public string GetCache()
		{
			if (CacheUpdated) CacheUpdated = false;

			return JsonConvert.SerializeObject(new ViewCache()
			{
				CompiledViews = compiledViews,
				ViewTemplates = viewTemplates,
				ViewDependencies = viewDependencies
			}, Formatting.Indented);
		}

		// adapted from: http://stackoverflow.com/a/8218033/170217
		private static bool CanOpenForRead(string filePath)
		{
			try
			{
				using (FileStream file = new FileStream(filePath, FileMode.Open, FileAccess.Read))
				{
					file.Close();
					return true;
				}
			}
			catch
			{
				return false;
			}
		}

		public string LoadView(string fullName, Dictionary<string, string> tags)
		{
			string result = null;

			var renderedView = viewCompiler.Render(fullName, tags);

			if (renderedView != null)
			{
				try
				{
					result = XDocument.Parse(renderedView.Result).ToString();
				}
				catch
				{
					// Oops, Html is not well formed, probably tried to parse a fragment
					// that had embedded string.Format placeholders or something weird.
					result = renderedView.Result;
				}
			}

			return result;
		}
	}
	#endregion

	#region EXTENSION METHODS / DYNAMIC DICTIONARY
	public static class ExtensionMethods
	{
		public static void ThrowIfArgumentNull<T>(this T t, string message = null)
		{
			string argName = t.GetType().Name;

			if (t == null)
				throw new ArgumentNullException(argName, message);
			else if ((t is string) && (t as string) == string.Empty)
				throw new ArgumentException(argName, message);
		}

		// from: http://blogs.msdn.com/b/csharpfaq/archive/2006/10/09/how-do-i-calculate-a-md5-hash-from-a-string_3f00_.aspx
		public static string CalculateMD5sum(this string input)
		{
			MD5 md5 = System.Security.Cryptography.MD5.Create();
			byte[] inputBytes = System.Text.Encoding.UTF8.GetBytes(input);
			byte[] hash = md5.ComputeHash(inputBytes);

			StringBuilder sb = new StringBuilder();

			for (int i = 0; i < hash.Length; i++)
				sb.Append(hash[i].ToString("X2"));

			return sb.ToString();
		}
	}
	#endregion
}