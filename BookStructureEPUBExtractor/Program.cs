using Mono.Options;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text.RegularExpressions;
using System.Configuration;
using System.Text;

namespace BookStructureEPUBExtractor
{
    internal class Program
    {
        // Input Data using this schema: applicationDescription: 3.6.4.4|deviceModel: iPad|language: English(United States)|operatingSystemVersion: 10.2.1|region: United States|errorType: SpineMissing|sourceURL: https://acs.cdn.overdrive.com/ACSStore1/0293-1/648/F2E/4F/%7B648F2E4F-F13A-4ED7-9D74-734526C55082%7DFmt410.epub
        private static readonly string OutputDirectory = ConfigurationManager.AppSettings["OutputDirectory"];
        private static readonly string ProductionAppVersion = ConfigurationManager.AppSettings["ProductionAppVersion"];

        private static void Main(string[] args)
        {
            string discoveryFileDirectory = null, currentParameter = string.Empty;
            bool showHelp = false, ignorePreviousVersions = false;

            var options = new OptionSet()
            {
                {
                    "d|discovery=", "File path of the CSV file containing titles to gather information about.", v =>
                    {
                        currentParameter = "d";
                        discoveryFileDirectory = v;
                    }
                },
                {
                    "i", "Use to ignore previous version numbers (list is contained in appConfig).",
                    v => { ignorePreviousVersions = true; }
                },
                {"h|help", "Help and additional information about commands", v => showHelp = v != null},
                {"<>", v => { }}
            };

            options.Parse(args);

            if (showHelp)
            {
                DisplayHelp(options);
                return;
            }

            if (string.IsNullOrEmpty(discoveryFileDirectory))
                DisplayHelp(options);

            try
            {
                if (!string.IsNullOrEmpty(discoveryFileDirectory) && currentParameter.Equals("d"))
                    DiscoverProblemTitleData(discoveryFileDirectory, ignorePreviousVersions);
            }
            catch (IOException ex)
            {
                Console.WriteLine($"An error has occurred: {ex.Message}");
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
            var problemEpubTitlesInfo = new Dictionary<string, DiscoveredProblemTitle>();
            var problemOpenEpubTitlesInfo = new Dictionary<string, DiscoveredProblemTitle>();
            var sideLoadedTitles = new List<UndiscoveredProblemTitle>();

            // If ignoring previous versions, then provide list of versions to ignore, otherwise leave empty.
            var versionsToIgnore = ignorePrevoiusVersions
                ? ConfigurationManager.AppSettings["VersionsToIgnore"].Split('|')
                : null;

            // Gather known titles to compare potential new titles against.
            var knownTitlesFile = new FileInfo($"{OutputDirectory}known.txt");
            var knownProblemTitles = new List<string>();
            var knownFileContents = string.Empty;

            if (knownTitlesFile.Exists)
            {
                using (var reader = new StreamReader(knownTitlesFile.FullName))
                {
                    var line = reader.ReadLine();
                    if (!string.IsNullOrEmpty(line))
                        knownFileContents = line;
                }

                if (!string.IsNullOrEmpty(knownFileContents))
                    knownProblemTitles = knownFileContents.Split('|').ToList();
            }

            Console.WriteLine("Processing data...");

            // Confirm the file and directory exist.
            if (!File.Exists(csvFilepath))
                throw new IOException("The specified file and/or directory does not exist: " + csvFilepath);

            // Read through the csv file
            using (var reader = new StreamReader(csvFilepath))
            {
                while (!reader.EndOfStream)
                {
                    // Grab the current line
                    var line = reader.ReadLine();

                    // Continue if current line contains sourceURL or line is empty/null.
                    if (string.IsNullOrEmpty(line) || !line.Contains("sourceURL"))
                        continue;

                    // Get the app version number if it is available.
                    var lineVersionNumber = line.Contains("applicationDescription")
                        ? line.Split('|')[0].Split(':')[1].Trim()
                        : ProductionAppVersion;

                    // The current line's app version is contained in the list to ignore, move to the next line.
                    if ((versionsToIgnore != null) && (versionsToIgnore.Length > 0))
                        if (versionsToIgnore.Any(version => lineVersionNumber.IndexOf(version, StringComparison.Ordinal) > -1))
                            continue;

                    // Ignore line if it is a pdf.
                    if (line.ToLower().Contains(".pdf"))
                        continue;

                    // Grab string between %7B through %7D excluding %7D.
                    var match = Regex.Match(line, "%7B.*?(?=%7D)");

                    if (match.Success) // Title was loaded in-app.
                    {
                        var titleInfo = match.Value.Substring(3);

                        // Current line is already known and reported.
                        if ((knownProblemTitles.Count > 0) && knownProblemTitles.Contains(titleInfo))
                            continue;

                        // Determine if title is Open EPUB, if so add to Open EPUB list.
                        if (line.ToLower().Contains("openepubstore"))
                        {
                            if (!problemOpenEpubTitlesInfo.ContainsKey(titleInfo))
                                problemOpenEpubTitlesInfo.Add(titleInfo,
                                    ConstructDiscoveredProblemTitleFromCsvFile(line, titleInfo, "open"));
                        }
                        // Determine if title is Adobe EPUB, if so add to Adobe EPUB list.
                        else
                        {
                            if (!problemEpubTitlesInfo.ContainsKey(titleInfo))
                                problemEpubTitlesInfo.Add(titleInfo,
                                    ConstructDiscoveredProblemTitleFromCsvFile(line, titleInfo, "adobe"));
                        }
                    }
                    else // title side-loaded
                    {
                        sideLoadedTitles.Add(ConstructUndiscoveredProblemTitleFromCsvFile(line));
                    }
                }
            }

            // Update known titles file.
            Console.WriteLine("Generating known title output...");
            using (var writer = new StreamWriter(knownTitlesFile.FullName, true))
            {
                foreach (var newTitle in problemEpubTitlesInfo)
                    writer.Write(newTitle.Key + '|');
            }

            // If there are no new titles, don't create an empty output file.
            if ((problemEpubTitlesInfo.Count > 0) || (problemOpenEpubTitlesInfo.Count > 0))
            {
                Console.WriteLine("Generating new title output...");
                var outputLocation = $"{OutputDirectory}{DateTime.UtcNow:yyyyMMddHHmmss}-titles-to-add.txt";
                using (var writer = new StreamWriter(outputLocation))
                {
                    // Output Adobe EPUB problem titles
                    foreach (var title in problemEpubTitlesInfo)
                        writer.WriteLine(title.Value.ToString());

                    //// Output Open EPUB problem titles
                    foreach (var title in problemOpenEpubTitlesInfo)
                        writer.WriteLine(title.Value.ToString());

                    // Output Side-loaded titles
                    writer.WriteLine("{0}{0}----{0}h5. Side-loaded Titles", Environment.NewLine);
                    sideLoadedTitles.ForEach(t => writer.WriteLine(t.ToString()));

                    Console.WriteLine("Output generated in the following location: " + outputLocation);
                }
            }
            else
                Console.WriteLine("There were no new titles to add.");

            // Archive the input file.
            ArchiveFile(csvFilepath);
        }

        /// <summary>
        /// Creates a DiscoveredProblemTitle based on bar (|) delimited data from a csv file.
        /// </summary>
        /// <param name="outputLineItem">CSV line item containing the '|' formated data from GA.</param>
        /// <param name="reserveId">Unique identifier of the problem title.</param>
        /// <param name="formatType">Format of the problem title.</param>
        /// <returns>DiscoveredProblemTitle</returns>
        private static DiscoveredProblemTitle ConstructDiscoveredProblemTitleFromCsvFile(string outputLineItem, string reserveId, string formatType)
        {
            var outputLineData = outputLineItem.Split('|');

            var positionInArray = Array.FindIndex(outputLineData, x => x.ToLower().Contains("applicationdescription"));
            var applicationDescription = positionInArray > -1
                ? new Version(outputLineData[positionInArray].Split(':')[1].Trim())
                : new Version(ProductionAppVersion);

            positionInArray = Array.FindIndex(outputLineData, x => x.ToLower().Contains("devicemodel"));
            var deviceModel = positionInArray > -1 ? outputLineData[positionInArray].Split(':')[1].Trim() : string.Empty;

            positionInArray = Array.FindIndex(outputLineData, x => x.ToLower().Contains("language"));
            var language = positionInArray > -1 ? outputLineData[positionInArray].Split(':')[1].Trim() : string.Empty;

            positionInArray = Array.FindIndex(outputLineData, x => x.ToLower().Contains("operatingsystemversion"));
            var orperatingSystemVersion = positionInArray > -1
                ? new Version(outputLineData[positionInArray].Split(':')[1].Trim())
                : new Version("9.3.1");

            positionInArray = Array.FindIndex(outputLineData, x => x.ToLower().Contains("region"));
            var region = positionInArray > -1 ? outputLineData[positionInArray].Split(':')[1].Trim() : string.Empty;

            positionInArray = Array.FindIndex(outputLineData, x => x.ToLower().Contains("errortype"));
            var errorType = positionInArray > -1 ? outputLineData[positionInArray].Split(':')[1].Trim() : string.Empty;

            var problemTitle = new DiscoveredProblemTitle(GetTitleMetadata(reserveId), applicationDescription, formatType)
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
            var applicationDescription = positionInArray > -1
                ? new Version(outputLineData[positionInArray].Split(':')[1].Trim())
                : new Version(ProductionAppVersion);

            positionInArray = Array.FindIndex(outputLineData, x => x.ToLower().Contains("devicemodel"));
            var deviceModel = positionInArray > -1 ? outputLineData[positionInArray].Split(':')[1].Trim() : string.Empty;

            positionInArray = Array.FindIndex(outputLineData, x => x.ToLower().Contains("language"));
            var language = positionInArray > -1 ? outputLineData[positionInArray].Split(':')[1].Trim() : string.Empty;

            positionInArray = Array.FindIndex(outputLineData, x => x.ToLower().Contains("operatingsystemversion"));
            var orperatingSystemVersion = positionInArray > -1
                ? new Version(outputLineData[positionInArray].Split(':')[1].Trim())
                : new Version("9.3.1");

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
        /// Given a discoverable problem title's ID, calls the Metadata service to obtain additional metadata about the title.
        /// </summary>
        /// <param name="reserveId">Discoverable Problem Title's unique identifier.</param>
        /// <returns>JSON formatted metadata pertaining to the passed in reserveId.</returns>
        private static string GetTitleMetadata(string reserveId)
        {
            string contents;
            using (var client = new WebClient())
            {
                client.Headers["Content-type"] = "application/json";
                client.Encoding = Encoding.UTF8;

                // Generate the correct URL (based on CRID)
                contents = client.DownloadString(string.Format(ConfigurationManager.AppSettings["MetadataEndpointUrl"], reserveId));
            }

            return contents;
        }

        /// <summary>
        /// Moves a given file to the archive directory and appends a datetime stamp to it.
        /// </summary>
        /// <param name="fileToMove">Directory and filename of the file to archive.</param>
        private static void ArchiveFile(string fileToMove)
        {
            var archiveDirectory = ConfigurationManager.AppSettings["ArchiveDirectory"];

            if (File.Exists(fileToMove))
                if (Directory.Exists(archiveDirectory))
                    File.Move(fileToMove,
                        Path.Combine(archiveDirectory,
                            $"{DateTime.UtcNow:yyyyMMddHHmmss}-{Path.GetFileName(fileToMove)}"));
                else
                    Console.WriteLine($"Directory does not exists: {archiveDirectory}");
            else
                Console.WriteLine($"Could not find the following file to move: {fileToMove}");
        }
    }
}