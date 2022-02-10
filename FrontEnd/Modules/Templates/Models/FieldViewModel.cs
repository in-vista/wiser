﻿using System.Collections.Generic;
using System.Reflection;
using GeeksCoreLibrary.Core.Cms.Attributes;

namespace FrontEnd.Modules.Templates.Models
{
    public class FieldViewModel
    {
        public string Name { get; set; }

        public object Value { get; set; }

        public Dictionary<string, List<FieldViewModel>> SubFields { get; set; }

        public PropertyInfo PropertyInfo { get; set; }

        public CmsPropertyAttribute CmsPropertyAttribute { get; set; }
    }
}
