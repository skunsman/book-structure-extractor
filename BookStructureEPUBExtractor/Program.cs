using Mono.Options;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text.RegularExpressions;
using System.Configuration;
using MoreLinq;

namespace BookStructureEPUBExtractor
{
    internal class Program
    {
        // Input Data using this schema: applicationDescription: 3.5.4.10|deviceModel: iPhone|language: français (Canada)|operatingSystemVersion: 8.4|region: États-Unis|sourceURL: http://acs.cdn.overdrive.com/ACSStore1/1071-1/370/E33/E5/%7B370E33E5-8B12-4521-B74A-79BC63D68687%7DFmt410.epub
        private static string _metadataEndpoint = "http://metadatasvc.hq.overdrive.com/MetadataService/v2/titles/{0}";
        private static string _outputDirectory = ConfigurationManager.AppSettings["OutputDirectory"];
        private static string _productionAppVersion = ConfigurationManager.AppSettings["ProductionAppVersion"];

        private static void Main(string[] args)
        {
            string discoveryFileDirectory = null, currentParameter = string.Empty;
            var compareList = new List<string>();
            bool showHelp = false, ignorePreviousVersions = false;

            var options = new OptionSet()
            {
                { "d|discovery=", "File path of the CSV file containing titles to gather information about.", v => { currentParameter = "d"; discoveryFileDirectory = v; } },
                { "n|new=", "Compare to lists of titles. Outputing a list of new titles to be added.", v => { currentParameter = "n"; compareList.Add(v); } },
                { "f|fixed=", "Compare files - a new list of problems titles against an exisiting list of fixed problem titles.", v => { currentParameter = "f"; compareList.Add(v); } },
                { "i", "Use to ignore previous version numbers (list is contained in appConfig).", v => { ignorePreviousVersions = true; } },
                { "h|help", "Help and additional information about commands", v => showHelp = v != null },
                { "<>", v => {
                    switch (currentParameter) {
                        case "n":
                        case "f":
                            var items = v.Split(' ').ToList<string>();
                            items.ForEach(i => compareList.Add(i));
                            break;
                    }}
                },
            };

            options.Parse(args);

            if (showHelp)
            {
                DisplayHelp(options);
                return;
            }

            if (string.IsNullOrEmpty(discoveryFileDirectory) && compareList.Count == 0)
                DisplayHelp(options);

            try
            {
                if (!string.IsNullOrEmpty(discoveryFileDirectory) && currentParameter.Equals("d"))
                    DiscoverProblemTitleData(discoveryFileDirectory, ignorePreviousVersions);

                if (compareList.Count > 0 && currentParameter.Equals("n"))
                    OutputNewTitlesToAdd(compareList);
            }
            catch (IOException ex)
            {
                Console.WriteLine($"An error has occurred: {ex.Message}");
                return;
            }
        }

        private static void DisplayHelp(OptionSet options)
        {
            Console.WriteLine("Options:");
            options.WriteOptionDescriptions(Console.Out);
        }

        /// <summary>
        /// Iterates through a CSV file to discover the type of problem title and potentially acquiring addition information about the title.
        /// </summary>
        /// <param name="csvFilepath">Directory in which the CSV file is located.</param>
        /// <param name="ignorePrevoiusVersions">Allows the abilty to ignore older versions of problem titles.</param>
        /// <remarks>Generates an text file containing a list of the different type of problem titles.</remarks>
        private static void DiscoverProblemTitleData(string csvFilepath, bool ignorePrevoiusVersions)
        {
            // Confirm the file and directory exist.
            if (!File.Exists(csvFilepath))
                throw new IOException("The specified file and/or directory does not exist: " + csvFilepath);

            var reader = new StreamReader(File.OpenRead(csvFilepath));
            var problemEpubTitlesInfo = new Dictionary<string, DiscoveredProblemTitle>();
            var problemOpenEpubTitlesInfo = new Dictionary<string, DiscoveredProblemTitle>();
            var sideLoadedTitles = new List<UndiscoveredProblemTitle>();

            // If ignoring previous versions, then provide list of versions to ignore, otherwise leave empty.
            var versionsToIgnore = ignorePrevoiusVersions ? ConfigurationManager.AppSettings["VersionsToIgnore"].Split('|') : null;

            Console.WriteLine("Processing data...");

            // Read through the csv file
            while (!reader.EndOfStream)
            {
                // Grab the current line
                var line = reader.ReadLine();

                // Continue if current line matches string
                if (line.Contains("sourceURL"))
                {
                    // Get the app version number if it is available.
                    var lineVersionNumber = line.Contains("applicationDescription")
                        ? line.Split('|')[0].Split(':')[1].Trim()
                        : _productionAppVersion;

                    // The current line's app version is contained in the list to ignore, move to the next line.
                    var skipLine = false;
                    if (versionsToIgnore != null && versionsToIgnore.Length > 0)
                    {
                        foreach (var version in versionsToIgnore)
                        {
                            if (lineVersionNumber.IndexOf(version) > -1)
                            {
                                skipLine = true;
                                break;
                            }
                        }
                    }

                    if (skipLine)
                        continue;

                    // Add to list of PDF titles if containing pdf extension.
                    if (line.ToLower().Contains(".pdf"))
                    {
                        //pdfTitles.Add(new UndiscoveredProblemTitle(line.Split('|')[1].Trim(), lineVersionNumber));
                        //pdfTitles.Add(ConstructUndiscoveredProblemTitleFromCsvFile(line));
                    }
                    else
                    {
                        // Grab string between %7B through %7D excluding %7D
                        var pattern = "%7B.*?(?=%7D)";
                        var titleInfo = string.Empty;

                        var match = Regex.Match(line, pattern);

                        if (match.Success) // Title was loaded in-app.
                        {
                            titleInfo = match.Value.Substring(3); // Exclude "%7B".

                            // Determine if title is Open EPUB, if so add to Open EPUB list.
                            if (line.ToLower().Contains("openepubstore"))
                            {
                                if (!problemOpenEpubTitlesInfo.ContainsKey(titleInfo))
                                    problemOpenEpubTitlesInfo.Add(titleInfo, ConstructDiscoveredProblemTitleFromCsvFile(line, titleInfo, "open"));
                            }
                            // Determine if title is Adobe EPUB, if so add to Adobe EPUB list.
                            else
                            {
                                if (!problemEpubTitlesInfo.ContainsKey(titleInfo))
                                    problemEpubTitlesInfo.Add(titleInfo, ConstructDiscoveredProblemTitleFromCsvFile(line, titleInfo, "adobe"));
                            }
                        }
                        else // title side-loaded
                        {
                            sideLoadedTitles.Add(ConstructUndiscoveredProblemTitleFromCsvFile(line));
                        }
                    }
                }
            }

            Console.WriteLine("Generating output...");
            var outputLocation = string.Format($"{_outputDirectory}new_{DateTime.Now:MM-dd-yy-mm-s}.txt");
            using (var writer = new StreamWriter(outputLocation))
            {
                // Output Adobe EPUB problem titles
                foreach (var title in problemEpubTitlesInfo)
                    writer.WriteLine(title.Value.ToString());

                //// Output Open EPUB problem titles
                foreach (var title in problemOpenEpubTitlesInfo)
                    writer.WriteLine(title.Value.ToString());

                // Output PDF titles
                //writer.WriteLine("{0}{0}----{0}PDF Titles", Environment.NewLine);
                //pdfTitles.ForEach(t => writer.WriteLine(t.ToString()));

                // Output Side-loaded titles
                writer.WriteLine("{0}{0}----{0}Side-loaded Titles", Environment.NewLine);
                sideLoadedTitles.ForEach(t => writer.WriteLine(t.ToString()));

                Console.WriteLine("Output generated in the following location: " + outputLocation);
            }
        }

        /// <summary>
        /// Given two lists of discoveredProblemTitles, compares the contents of each to determine whether new titles need to investigated.
        /// </summary>
        /// <param name="listsToCompare">Two file directories leading to text files containing a list of discovered problem titles. First list - Known titles. Second list - New titles (from GA report).</param>
        /// <remarks>If new problem titles exist, a text file is generated containing the list of new titles to be investigated.</remarks>
        private static void OutputNewTitlesToAdd(List<string> listsToCompare)
        {
            string newTitlesFilepath = listsToCompare[0], existingTitlesFilepath = listsToCompare[1];

            var newTitles = ReadFileForDiscoveredProblemTitles(newTitlesFilepath);
            var existingTitles = ReadFileForDiscoveredProblemTitles(existingTitlesFilepath);

            // List of existingTitles' TitleIds
            var titleIds = new HashSet<string>(existingTitles.Select(t => t.TitleId));

            // Gather titles from the new list that do not exist in the existing list.
            var titlesToAdd = newTitles.Where(t => !titleIds.Contains(t.TitleId)).ToList();
            var outputGenerated = false;

            // Generate output if there are new titles to be added.
            if (titlesToAdd.Count > 0)
            {
                var outputLocation = string.Format($"{_outputDirectory}titlesToAdd_{DateTime.Now:MM-dd-yy-mm-s}.txt");
                using (var writer = new StreamWriter(outputLocation))
                {
                    titlesToAdd.ForEach(t => writer.WriteLine(t.ToString()));
                    Console.WriteLine("A list of new titles to add was generated in the following location: " + outputLocation);
                    outputGenerated = true;
                }
            }

            if (!outputGenerated)
                Console.WriteLine("No output was generated. There were no new titles to add to the list.");
        }

        /// <summary>
        /// Creates a DiscoveredProblemTitle based on bar (|) delimited data from a csv file.
        /// </summary>
        /// <param name="outputLineItem">CSV line item containing the '|' formated data from GA.</param>
        /// <returns>DiscoveredProblemTitle</returns>
        private static DiscoveredProblemTitle ConstructDiscoveredProblemTitleFromCsvFile(string outputLineItem, string titleId, string formatType)
        {
            var outputLineData = outputLineItem.Split('|');

            var positionInArray = Array.FindIndex(outputLineData, x => x.ToLower().Contains("applicationdescription"));
            var applicationDescription = positionInArray > -1 ? new Version(outputLineData[positionInArray].Split(':')[1].Trim()) : new Version(_productionAppVersion);

            positionInArray = Array.FindIndex(outputLineData, x => x.ToLower().Contains("devicemodel"));
            var deviceModel = positionInArray > -1 ? outputLineData[positionInArray].Split(':')[1].Trim() : string.Empty;

            positionInArray = Array.FindIndex(outputLineData, x => x.ToLower().Contains("language"));
            var language = positionInArray > -1 ? outputLineData[positionInArray].Split(':')[1].Trim() : string.Empty;

            positionInArray = Array.FindIndex(outputLineData, x => x.ToLower().Contains("operatingsystemversion"));
            var orperatingSystemVersion = positionInArray > -1 ? new Version(outputLineData[positionInArray].Split(':')[1].Trim()) : new Version("9.3.1");

            positionInArray = Array.FindIndex(outputLineData, x => x.ToLower().Contains("region"));
            var region = positionInArray > -1 ? outputLineData[positionInArray].Split(':')[1].Trim() : string.Empty;

            positionInArray = Array.FindIndex(outputLineData, x => x.ToLower().Contains("errortype"));
            var errorType = positionInArray > -1 ? outputLineData[positionInArray].Split(':')[1].Trim() : string.Empty;

            var problemTitle = new DiscoveredProblemTitle(GetTitleMetadata(titleId), applicationDescription, formatType)
            {
                DeviceModel = deviceModel,
                Language = language,
                OperatingSystemVersion = orperatingSystemVersion,
                Region = region,
                ErrorType = errorType
            };

            return problemTitle;
        }

        /// <summary>
        /// Creates a UndiscoveredProblemTitle based on bar (|) delimited data from a csv file.
        /// </summary>
        /// <param name="outputLineItem">CSV line item containing the '|' formated data from GA.</param>
        /// <returns>UndiscoveredProblemTitle</returns>
        private static UndiscoveredProblemTitle ConstructUndiscoveredProblemTitleFromCsvFile(string outputLineItem)
        {
            string data;
            var outputLineData = outputLineItem.Split('|');

            var positionInArray = Array.FindIndex(outputLineData, x => x.ToLower().Contains("applicationdescription"));
            var applicationDescription = positionInArray > -1 ? new Version(outputLineData[positionInArray].Split(':')[1].Trim()) : new Version(_productionAppVersion);

            positionInArray = Array.FindIndex(outputLineData, x => x.ToLower().Contains("devicemodel"));
            var deviceModel = positionInArray > -1 ? outputLineData[positionInArray].Split(':')[1].Trim() : string.Empty;

            positionInArray = Array.FindIndex(outputLineData, x => x.ToLower().Contains("language"));
            var language = positionInArray > -1 ? outputLineData[positionInArray].Split(':')[1].Trim() : string.Empty;

            positionInArray = Array.FindIndex(outputLineData, x => x.ToLower().Contains("operatingsystemversion"));
            var orperatingSystemVersion = positionInArray > -1 ? new Version(outputLineData[positionInArray].Split(':')[1].Trim()) : new Version("9.3.1");

            positionInArray = Array.FindIndex(outputLineData, x => x.ToLower().Contains("region"));
            var region = positionInArray > -1 ? outputLineData[positionInArray].Split(':')[1].Trim() : string.Empty;

            positionInArray = Array.FindIndex(outputLineData, x => x.ToLower().Contains("errorType"));
            var errorType = positionInArray > -1 ? outputLineData[positionInArray].Split(':')[1].Trim() : string.Empty;

            positionInArray = Array.FindIndex(outputLineData, x => x.ToLower().Contains("sourceurl"));

            if (positionInArray > -1)
            {
                var match = Regex.Match(outputLineData[positionInArray], "Inbox/.*?.epub");
                data = match.Success ? match.Value.Substring(6) : outputLineData[positionInArray];
            }
            else
                data = string.Empty;

            var undiscoveredProblemTitle = new UndiscoveredProblemTitle
            {
                AppVersion = applicationDescription,
                DeviceModel = deviceModel,
                Language = language,
                OperatingSystemVersion = orperatingSystemVersion,
                Region = region,
                ErrorType = errorType,
                Data = data
            };

            return undiscoveredProblemTitle;
        }

        /// <summary>
        /// Given a valid directory, iterates through a text file to gather a list of discovered problem titles.
        /// </summary>
        /// <param name="fileDirectory">Text file directory containing a list of discovered problem titles in '|' format.</param>
        /// <returns>A list of DiscoveredProblemTitles</returns>
        private static IEnumerable<DiscoveredProblemTitle> ReadFileForDiscoveredProblemTitles(string fileDirectory)
        {
            var problemTitles = new List<DiscoveredProblemTitle>();

            // Confirm the file and directory exist.
            if (!File.Exists(fileDirectory))
                throw new IOException("The specified file and/or directory does not exist: " + fileDirectory);

            var reader = new StreamReader(File.OpenRead(fileDirectory));
            while (!reader.EndOfStream)
            {
                var line = reader.ReadLine();
                if (string.IsNullOrEmpty(line))
                    break;
                else
                    problemTitles.Add(ConstructDiscoveredProblemTitleFromOutputFile(line));
            }

            return problemTitles;
        }

        /// <summary>
        /// Properly reconstructs a DiscoveredProblemTitle from a line item of generated outputted problem title data.
        /// </summary>
        /// <param name="outputLineItem">Line item containing the outputted problem title in '|' format.</param>
        /// <returns>DiscoveredProblemTitle</returns>
        private static DiscoveredProblemTitle ConstructDiscoveredProblemTitleFromOutputFile(string outputLineItem)
        {
            var outputLineData = outputLineItem.Split('|');
            
            var problemTitle = new DiscoveredProblemTitle
            {
                Title = outputLineData[0].Substring(3).Trim(),
                Crid = outputLineData[2].Trim(),
                TitleId = outputLineData[3].Trim(),
                FormatType = outputLineData[4].Trim(),
                Publisher = outputLineData[5].Trim(),
                AppVersion = new Version(outputLineData[6].Trim()),
                OperatingSystemVersion = new Version(outputLineData[7].Trim()),
                Language = outputLineData[8].Trim(),
                Region = outputLineData[9].Trim(),
                ErrorType = outputLineData[10].Trim()
            };

            return problemTitle;
        }

        /// <summary>
        /// Given a discoverable problem title's ID, calls the Metadata service to obtain additional metadata about the title.
        /// </summary>
        /// <param name="titleId">Discoverable Problem Title's unique identifier.</param>
        /// <returns>JSON formatted metadata pertaining to the passed in titleID.</returns>
        private static string GetTitleMetadata(string titleId)
        {
            var contents = string.Empty;
            using (var client = new WebClient())
            {
                client.Headers["Content-type"] = "application/json";

                // Generate the correct URL (based on CRID)
                contents = client.DownloadString(string.Format(_metadataEndpoint, titleId));
            }

            return contents;
        }
    }
}
