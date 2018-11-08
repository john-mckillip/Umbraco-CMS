﻿using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Umbraco.Core.Persistence;
using Umbraco.Core.Persistence.Dtos;
using Umbraco.Core.PropertyEditors;
using Umbraco.Core.Logging;
using Umbraco.Core.Models;

namespace Umbraco.Core.Migrations.Upgrade.V_8_0_0
{
    public class DropDownPropertyEditorsMigration : MigrationBase
    {
        public DropDownPropertyEditorsMigration(IMigrationContext context) : base(context)
        {
        }

        public override void Migrate()
        {
            //need to convert the old drop down data types to use the new one
            var oldDropDowns = Database.Fetch<DataTypeDto>(Sql()
                .Select<DataTypeDto>()
                .From<DataTypeDto>()
                .Where<DataTypeDto>(x => x.EditorAlias.Contains(".DropDown")));
            foreach (var dd in oldDropDowns)
            {
                //nothing to change if there is no config
                if (dd.Configuration.IsNullOrWhiteSpace()) continue;

                ValueListConfiguration config;
                try
                {
                    config = JsonConvert.DeserializeObject<ValueListConfiguration>(dd.Configuration);
                }
                catch (Exception ex)
                {
                    Logger.Error<DropDownPropertyEditorsMigration>(
                        ex, "Invalid drop down configuration detected: \"{Configuration}\", cannot convert editor, values will be cleared",
                        dd.Configuration);
                    dd.Configuration = null;
                    Database.Update(dd);
                    continue;
                }

                var propDataSql = Sql().Select<PropertyDataDto>().From<PropertyDataDto>()
                    .InnerJoin<PropertyTypeDto>().On<PropertyTypeDto, PropertyDataDto>(x => x.Id, x => x.PropertyTypeId)
                    .InnerJoin<DataTypeDto>().On<DataTypeDto, PropertyTypeDto>(x => x.NodeId, x => x.DataTypeId)
                    .Where<PropertyTypeDto>(x => x.DataTypeId == dd.NodeId);

                var propDatas = Database.Query<PropertyDataDto>(propDataSql);
                var toUpdate = new List<PropertyDataDto>();
                foreach (var propData in propDatas)
                {
                    if (UpdatePropertyDataDto(propData, config))
                    {
                        //update later, we are iterating all values right now so no SQL can be run inside of this iteration (i.e. Query<T>)
                        toUpdate.Add(propData);
                    }
                }

                //run the property data updates
                foreach (var propData in toUpdate)
                {
                    Database.Update(propData);
                }

                var requiresCacheRebuild = false;
                switch (dd.EditorAlias)
                {
                    case string ea when ea.InvariantEquals("Umbraco.DropDown"):
                        UpdateDataType(dd, config, false);
                        break;
                    case string ea when ea.InvariantEquals("Umbraco.DropdownlistPublishingKeys"):
                        UpdateDataType(dd, config, false);
                        requiresCacheRebuild = true;
                        break;
                    case string ea when ea.InvariantEquals("Umbraco.DropDownMultiple"):
                        UpdateDataType(dd, config, true);
                        break;
                    case string ea when ea.InvariantEquals("Umbraco.DropdownlistMultiplePublishKeys"):
                        UpdateDataType(dd, config, true);
                        requiresCacheRebuild = true;
                        break;
                }

                if (requiresCacheRebuild)
                {
                    //TODO: How to force rebuild the cache?
                }
            }
        }

        private void UpdateDataType(DataTypeDto dataType, ValueListConfiguration config, bool isMultiple)
        {
            dataType.EditorAlias = Constants.PropertyEditors.Aliases.DropDownListFlexible;
            var flexConfig = new
            {
                multiple = isMultiple,
                items = config.Items
            };
            dataType.DbType = ValueStorageType.Nvarchar.ToString();
            dataType.Configuration = JsonConvert.SerializeObject(flexConfig);
            Database.Update(dataType);
        }

        private bool UpdatePropertyDataDto(PropertyDataDto propData, ValueListConfiguration config)
        {
            //Get the INT ids stored for this property/drop down
            int[] ids = null;
            if (!propData.VarcharValue.IsNullOrWhiteSpace())
            {
                ids = ConvertStringValues(propData.VarcharValue);
            }
            else if (!propData.TextValue.IsNullOrWhiteSpace())
            {
                ids = ConvertStringValues(propData.TextValue);
            }
            else if (propData.IntegerValue.HasValue)
            {
                ids = new[] { propData.IntegerValue.Value };
            }

            //if there are INT ids, convert them to values based on the configured pre-values
            if (ids != null && ids.Length > 0)
            {
                //map the ids to values
                var vals = new List<string>();
                var canConvert = true;
                foreach (var id in ids)
                {
                    var val = config.Items.FirstOrDefault(x => x.Id == id);
                    if (val != null)
                        vals.Add(val.Value);
                    else
                    {
                        Logger.Warn<DropDownPropertyEditorsMigration>(
                            "Could not find associated data type configuration for stored Id {DataTypeId}", id);
                        canConvert = false;
                    }   
                }
                if (canConvert)
                {
                    propData.VarcharValue = string.Join(",", vals);
                    propData.TextValue = null;
                    propData.IntegerValue = null;
                    return true;
                }
                
            }

            return false;
        }

        private int[] ConvertStringValues(string val)
        {
            var splitVals = val.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries);

            var intVals = splitVals
                            .Select(x => int.TryParse(x, out var i) ? i : int.MinValue)
                            .Where(x => x != int.MinValue)
                            .ToArray();

            //only return if the number of values are the same (i.e. All INTs)
            if (splitVals.Length == intVals.Length)
                return intVals;

            return null;
        }

    }
}