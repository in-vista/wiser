using System.Collections.Generic;
using System.Xml.Linq;
using FrontEnd.Modules.Templates.Models.WtsModels;

namespace FrontEnd.Modules.Templates.Models;

public class WtsConfigurationTabViewModel
{
    //note: class doesn't appear to currently be in use.
    public string ServiceName { get; set; }
    public string ConnectionString { get; set; }
    public LogSettings LogSettings { get; set; }
    public List<RunScheme> RunSchemes { get; set; }
    public string[] LogMinimumLevels { get; set; }
    public string[] RunSchemeTypes { get; set; }
    public string ChildItemsExtraString { get; set; }
}