﻿using CASCBruteforcer.Helpers;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading.Tasks;

namespace CASCBruteforcer.Bruteforcers
{
	class MixMatch : IHash
	{
		const string CHECKFILES_URL = "https://bnet.marlam.in/checkFiles.php";

		private ListfileHandler ListfileHandler;
		private int MaxDepth;
		private List<string[]> FileFilter;
		private string Extension;
		private string[] FileNames;

		readonly private string[] Unwanted = new[]
		{
			"\\EXPANSION00\\", "\\EXPANSION01\\", "\\EXPANSION02\\", "\\EXPANSION03\\", "\\EXPANSION04\\", "\\EXPANSION05\\",
			"\\NORTHREND\\", "\\CATACLYSM\\", "\\PANDARIA\\", "\\OUTLAND\\","\\PANDAREN\\",
			"WORLD\\MAPTEXTURES\\", "WORLD\\MINIMAPS\\", "CHARACTER\\", "\\BAKEDNPCTEXTURES\\", "COMPONENTS\\"
		};

		private HashSet<ulong> TargetHashes;
		private ConcurrentQueue<string> ResultStrings;

		public void LoadParameters(params string[] args)
		{
			if (args.Length == 1 || string.IsNullOrWhiteSpace(args[1]) || args[1].Trim() == "%")
				throw new ArgumentException("No filter provided.");
			if (args[1].Count(x => x == '%') > 1)
				throw new ArgumentException("Filter can't have more than one wildcard character.");

			FileFilter = new List<string[]>();

			if (File.Exists(args[1]))
			{
				var lines = File.ReadAllLines(args[1]);
				foreach (var l in lines)
				{
					if (string.IsNullOrWhiteSpace(l))
						continue;

					// attempt to remove extensions
					string filter = Normalise(l.Trim());
					if (filter.LastIndexOf('.') >= filter.Length - 5)
					{
						Extension = Normalise(filter.Substring(filter.LastIndexOf('.')));
						filter = filter.Substring(0, filter.LastIndexOf('.'));
					}

					FileFilter.Add(filter.Split(new[] { "%" }, StringSplitOptions.RemoveEmptyEntries));
				}
			}
			else
			{
				// attempt to remove extensions
				string filter = Normalise(args[1].Trim());
				if (filter.LastIndexOf('.') >= filter.Length - 5)
				{
					Extension = Normalise(filter.Substring(filter.LastIndexOf('.')));
					filter = filter.Substring(0, filter.LastIndexOf('.'));
				}

				FileFilter.Add(filter.Split(new[] { "%" }, StringSplitOptions.RemoveEmptyEntries));
			}

			if (args.Length > 2 && !string.IsNullOrWhiteSpace(args[2]))
				Extension = Normalise("." + args[2].Trim().TrimStart('.'));

			if (args.Length < 4 || !int.TryParse(args[3], out MaxDepth))
				MaxDepth = 5;
			if (MaxDepth < 1)
				MaxDepth = 1;

			ListfileHandler = new ListfileHandler();
			if (!ListfileHandler.GetKnownListfile(out string listfile))
				throw new Exception("No known listfile found.");

			FileNames = File.ReadAllLines(listfile).Select(x => Normalise(x)).Distinct().ToArray();

			ResultStrings = new ConcurrentQueue<string>();
			ParseHashes();
		}

		public void Start()
		{
			Run();
			LogAndExport();
		}

		private void Run()
		{
			Console.WriteLine("Loading Dictionary...");

			bool checkExtension = !string.IsNullOrWhiteSpace(Extension);

			// store all line endings
			HashSet<string> endings = new HashSet<string>();
			foreach (var o in FileNames)
			{
				int _s = o.Length - o.Replace("_", "").Length; // underscore count
				var parts = Path.GetFileName(o).Split('_').Reverse();

				if (checkExtension && Path.GetExtension(o) != Extension)
					continue;

				for (int i = 1; i <= _s && i <= MaxDepth; i++) // split by _s up to depth
				{
					string part = string.Join("_", parts.Take(i).Reverse());
					endings.Add(part); // exclude prefixed underscore
					endings.Add("_" + part); // prefix underscore
				}
			}

			Console.WriteLine("Loading Filenames...");

            // load files we want to permute
            var filterednames = FileNames.Where(x => /*!Unwanted.Any(y => x.Contains(y)) &&*/ ContainsFilter(x))
                                         .Concat(FileFilter.SelectMany(x => x))
                                         .Distinct();

            Queue<string> formattednames = new Queue<string>(filterednames);
			HashSet<string> usedBaseNames = new HashSet<string>();

			Console.WriteLine($"Starting MixMatch ");
			while (formattednames.Count > 0)
			{
				string o = formattednames.Dequeue();
				int _s = o.Length - o.Replace("_", "").Length; // underscore count

				var parts = Path.GetFileNameWithoutExtension(o).Split('_').Reverse();
				string path = Path.GetDirectoryName(o);

				// suffix known endings at each underscore
				for (int i = 0; i <= _s && i <= MaxDepth; i++)
				{
					string temp = "\\" + string.Join("_", parts.Skip(i).Reverse());

					if (usedBaseNames.Contains(path + temp))
						continue;

					Parallel.ForEach(endings, e =>
					{
						Validate(path + temp + e);
						Validate(path + "_" + temp + e);
					});

					usedBaseNames.Add(path + temp);
				}
			}
		}


		#region Validation
		private void Validate(string file)
		{
			var j = new JenkinsHash();
			ulong hash = j.ComputeHash(file);
			if (TargetHashes.Contains(hash))
				ResultStrings.Enqueue(file);
		}

		private void PostResults()
		{
			const int TAKE = 20000;

			int count = (int)Math.Ceiling(ResultStrings.Count / (float)TAKE);
			for (int i = 0; i < count; i++)
			{
				try
				{
					byte[] data = Encoding.ASCII.GetBytes("files=" + string.Join("\r\n", ResultStrings.Skip(i * TAKE).Take(TAKE)));

					HttpWebRequest req = (HttpWebRequest)WebRequest.Create(CHECKFILES_URL);
					req.Method = "POST";
					req.ContentType = "application/x-www-form-urlencoded";
					req.ContentLength = data.Length;
					req.UserAgent = "CASCBruteforcer/1.0 (+https://github.com/barncastle/CASC-Bruteforcer)"; // for tracking purposes
					using (var stream = req.GetRequestStream())
					{
						stream.Write(data, 0, data.Length);
						req.GetResponse(); // send the post
					}

					req.Abort();
				}
				catch { }
			}
		}

		private void LogAndExport()
		{
			ResultStrings = new ConcurrentQueue<string>(ResultStrings.Distinct());

			// log completion
			Console.WriteLine($"Found {ResultStrings.Count}:");

			if (ResultStrings.Count > 0)
			{
				// print to the screen
				foreach (var r in ResultStrings)
					Console.WriteLine($"  {r.Replace("\\", "/").ToLower()}");

				// write to Output.txt
				using (var sw = new StreamWriter(File.OpenWrite("Output.txt")))
				{
					sw.BaseStream.Position = sw.BaseStream.Length;
					foreach (var r in ResultStrings)
						sw.WriteLine(r.Replace("\\", "/").ToLower());
				}

				// post to Marlamin's site
				PostResults();
			}

			Console.WriteLine("");
		}

		#endregion


		#region Unknown Hash Functions
		private void ParseHashes()
		{
			bool parseListfile = ListfileHandler.GetUnknownListfile("unk_listfile.txt", "");
			if (parseListfile)
			{
				string[] lines = new string[0];

				// sanity check it actually exists
				if (File.Exists("unk_listfile.txt"))
					lines = File.ReadAllLines("unk_listfile.txt");

				// parse items - hex and standard because why not
				ulong dump = 0;
				IEnumerable<ulong> hashes = new ulong[0]; // 0 hash is used as a dump
#if DEBUG
				hashes = hashes.Concat(new ulong[] { 4097458660625243137, 13345699920692943597 }); // test hashes for the README examples
#endif
				hashes = hashes.Concat(lines.Where(x => ulong.TryParse(x.Trim(), NumberStyles.HexNumber, null, out dump)).Select(x => dump)); // hex
				hashes = hashes.Concat(lines.Where(x => ulong.TryParse(x.Trim(), out dump)).Select(x => dump)); // standard
				hashes = hashes.Distinct().OrderBy(Jenkins96.HashSort).ThenBy(x => x);

				TargetHashes = hashes.ToHashSet();                
			}

			if (TargetHashes == null || TargetHashes.Count < 1)
				throw new ArgumentException("Unknown listfile is missing or empty");
		}

		#endregion

		#region Helpers
		private string Normalise(string s) => s.Trim().Replace("/", "\\").ToUpperInvariant();

		private bool ContainsFilter(string x)
		{
			foreach (var filter in FileFilter)
			{
				if (filter.Length == 1)
				{
					if (x.Contains(filter[0]))
						return true;

					continue;
				}

				int f1 = x.IndexOf(filter[0]), f2 = x.IndexOf(filter[1]);
				if (f1 > -1 && f2 > -1 && f1 <= f2)
					return true;
			}

			return false;
		}

		#endregion
	}
}
