﻿using System;
using System.Security.Cryptography;
using Newtonsoft.Json.Linq;

namespace BookStructureEPUBExtractor
{
    public class ProblemTitle
    {
        #region Properties

        public Version AppVersion { get; set; }

        public string DeviceModel { get; set; }

        public string Language { get; set; }

        public Version OperatingSystemVersion { get; set; }

        public string Region { get; set; }

        public string ErrorType { get; set; }

        #endregion

        #region Methods

        public override string ToString()
        {
            return $"{AppVersion} | {OperatingSystemVersion} | {Language} | {Region} | {ErrorType}";
        }

        #endregion
    }

    public class DiscoveredProblemTitle : ProblemTitle
    {
        #region Constructors
        public DiscoveredProblemTitle()
        { }

        public DiscoveredProblemTitle(string jsonData, Version appVersion, string formatType)
        {
            var jObject = JObject.Parse(jsonData);
            Title = jObject.SelectToken("Title").ToString();
            Crid = jObject.SelectToken("ReserveID").ToString();
            TitleId = jObject.SelectToken("TitleID").ToString();
            Publisher = jObject.SelectToken("Publisher").ToString();
            AppVersion = appVersion;
            FormatType = formatType;
        }

        #endregion

        #region Properties
        public string Title { get; set; }

        public string TitleId { get; set; }

        public string Crid { get; set; }

        public string FormatType { get; set; }

        public string Publisher { get; set; }

        public string QaIntegrationUrl => string.Format($"https://qaintegration.overdrive.com/media/{TitleId}");
        
        public string MarketplaceUrl => string.Format($"[Marketplace|https://marketplace.overdrive.com/Marketplace/OneCopyOneUserAndMeteredAccess/TitleDetails/{Crid}]");

        public string MyDigitalLibraryUrl => string.Format($"[My Digital Library|http://mydigitallibrary.lib.overdrive.com/ContentDetails.htm?id={Crid}]");

        #endregion

        #region Methods
        public override string ToString()
        {
            return string.Format($"- [{Title}|{QaIntegrationUrl}] | {Crid} | {TitleId} | {FormatType} | {Publisher} | {base.ToString()} | {MarketplaceUrl}").TrimEnd(new char[] { ' ', '|' });
        }

        #endregion
    }

    public class UndiscoveredProblemTitle : ProblemTitle
    {
        #region Constructors

        public UndiscoveredProblemTitle()
        { }

        public UndiscoveredProblemTitle(string data, string appVersion)
        {
            Data = data;
            AppVersion = new Version(appVersion);
        }

        #endregion

        #region Properties

        public string Data { get; set; }

        #endregion

        #region Methods
        public override string ToString()
        {
            return string.Format($"- {Data} | {base.ToString()}").TrimEnd(new char[] { ' ', '|' });
        }

        #endregion
    }
}
