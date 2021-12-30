﻿using System.Collections.Generic;

namespace FrontEnd.Core.Models
{
    /// <summary>
    /// A model for settings for the Wiser front-end. Meant to be used in an IOptions pattern.
    /// </summary>
    public class FrontEndSettings
    {
        private string apiBaseUrl;

        /// <summary>
        /// Gets or sets the base URL of the Wiser API.
        /// </summary>
        public string ApiBaseUrl
        {
            get => apiBaseUrl;
            set
            {
                apiBaseUrl = value;
                if (!apiBaseUrl.EndsWith("/"))
                {
                    apiBaseUrl += "/";
                }
            }
        }

        /// <summary>
        /// Gets or sets the default client ID for the API.
        /// This is a generic ID that is used for all OAUTH2 authentication, this is separate from the user credentials.
        /// </summary>
        public string ApiClientId { get; set; }

        /// <summary>
        /// Gets or sets the default client secret for the API.
        /// This is a generic secret that is used for all OAUTH2 authentication, this is separate from the user credentials.
        /// </summary>
        public string ApiClientSecret { get; set; }

        /// <summary>
        /// Gets or sets the token for Track JS. If you don't want to use Track JS, you can leave this empty and the script will not be loaded.
        /// </summary>
        public string TrackJsToken { get; set; }

        /// <summary>
        /// Gets or sets the token for Marker.io. If you don't want to use Marker.io, you can leave this empty and the script will not be loaded.
        /// </summary>
        public string MarkerIoToken { get; set; }

        /// <summary>
        /// Gets or sets the host names that are used for the Wiser front-end. This should not include the sub domain.
        /// This will be used to figure out the sub domain, which is needed to find out which customer is being loaded.
        /// </summary>
        public List<string> WiserHostNames { get; set; } = new();
    }
}