﻿using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using DotnetSpider.Core;
using DotnetSpider.Extension.Model;
using DotnetSpider.Extension.Model.Attribute;
using DotnetSpider.Extension.Model.Formatter;

using DotnetSpider.Core.Infrastructure;
using DotnetSpider.Extension.Processor;
using DotnetSpider.Extension.Pipeline;
using NLog;
using DotnetSpider.Core.Pipeline;

namespace DotnetSpider.Extension
{
	public abstract class EntitySpider : CommonSpider
	{
		private static readonly List<string> DefaultProperties = new List<string> { "cdate", Core.Environment.IdColumn };

		public List<EntityDefine> Entities { get; internal set; } = new List<EntityDefine>();

		protected EntitySpider(string name) : this(name, new Site())
		{
		}

		protected EntitySpider(string name, Site site) : base(name, site)
		{
		}

		public EntityDefine AddEntityType(Type type, string tableName = null)
		{
			return AddEntityType(type, null, tableName);
		}

		public EntityDefine AddEntityType<T>(string tableName = null)
		{
			return AddEntityType(typeof(T), null, tableName);
		}

		public EntityDefine AddEntityType<T>(DataHandler dataHandler)
		{
			return AddEntityType(typeof(T), dataHandler);
		}

		public EntityDefine AddEntityType(Type type, DataHandler dataHandler, string tableName = null)
		{
			CheckIfRunning();

			if (typeof(SpiderEntity).IsAssignableFrom(type))
			{
				var entity = GenerateEntityDefine(type.GetTypeInfoCrossPlatform());

				entity.DataHandler = dataHandler;

				entity.SharedValues = type.GetTypeInfo().GetCustomAttributes<SharedValueSelector>().Select(e => new SharedValueSelector
				{
					Name = e.Name,
					Expression = e.Expression,
					Type = e.Type
				}).ToList();

				if (entity.TableInfo != null && !string.IsNullOrEmpty(tableName))
				{
					entity.TableInfo.Name = tableName;
				}

				Entities.Add(entity);
				EntityProcessor processor = new EntityProcessor(Site, entity);
				AddPageProcessor(processor);
				return entity;
			}
			else
			{
				throw new SpiderException($"Type: {type.FullName} is not a SpiderEntity.");
			}
		}

		public static EntityDefine GenerateEntityDefine(
#if !NET_CORE
			Type entityType
#else
			TypeInfo entityType
#endif
		)
		{
			EntityDefine entityDefine = new EntityDefine();
			GenerateEntityColumns(entityType, entityDefine);

			entityDefine.TableInfo = entityType.GetCustomAttribute<EntityTable>();

			if (entityDefine.TableInfo != null)
			{
				if (entityDefine.TableInfo.Indexs != null)
				{
					entityDefine.TableInfo.Indexs = new HashSet<string>(entityDefine.TableInfo.Indexs.Select(i => i.Replace(" ", ""))).ToArray();
				}
				if (entityDefine.TableInfo.Uniques != null)
				{
					entityDefine.TableInfo.Uniques = new HashSet<string>(entityDefine.TableInfo.Uniques.Select(i => i.Replace(" ", ""))).ToArray();
				}
			}
			EntitySelector entitySelector = entityType.GetCustomAttribute<EntitySelector>();
			if (entitySelector != null)
			{
				entityDefine.Multi = true;
				entityDefine.Take = entitySelector.Take;
				entityDefine.Selector = new BaseSelector { Expression = entitySelector.Expression, Type = entitySelector.Type };
			}
			else
			{
				entityDefine.Multi = false;
			}
			var targetUrlsSelectors = entityType.GetCustomAttributes<TargetUrlsSelector>();
			entityDefine.TargetUrlsSelectors = targetUrlsSelectors.ToList();

			ValidateEntityDefine(entityDefine);

			return entityDefine;
		}

		public static EntityDefine GenerateEntityColumns(
#if !NET_CORE
			Type entityType
#else
			TypeInfo entityType
#endif
			, EntityDefine entity
		)
		{
			var typeName = entityType.GetTypeCrossPlatform().FullName;
			entity.Name = typeName;

			var properties = entityType.GetProperties();
			if (properties.Any(p => DefaultProperties.Contains(p.Name.ToLower())))
			{
				throw new SpiderException("cdate is not available because it's a default property.");
			}
			foreach (var propertyInfo in properties)
			{
				var propertySelector = propertyInfo.GetCustomAttribute<PropertyDefine>();
				if (propertySelector == null)
				{
					continue;
				}

				var type = propertyInfo.PropertyType;

				var column = new Column
				{
					Multi = typeof(IList).IsAssignableFrom(type),
					Option = propertySelector.Option,
					Selector = new BaseSelector
					{
						Expression = propertySelector.Expression,
						Type = propertySelector.Type,
						Argument = propertySelector.Argument
					},
					NotNull = propertySelector.NotNull,
					IgnoreStore = propertySelector.IgnoreStore,
					Length = propertySelector.Length,
					Name = propertyInfo.Name
				};

				foreach (var formatter in propertyInfo.GetCustomAttributes<Formatter>(true))
				{
					column.Formatters.Add(formatter);
				}

				var targetUrl = propertyInfo.GetCustomAttribute<LinkToNext>();
				if (targetUrl != null)
				{
					targetUrl.PropertyName = column.Name;
					entity.LinkToNexts.Add(targetUrl);
				}

				column.DataType = type.Name;

				if (column.DataType != DataTypeNames.String && propertySelector.Length > 0)
				{
					throw new SpiderException("Only string property can set length.");
				}

				entity.Columns.Add(column);
			}
			return entity;
		}

		protected override IPipeline GetDefaultPipeline()
		{
			IPipeline pipeline;
			switch (Core.Environment.DataConnectionStringSettings.ProviderName)
			{
				case "Npgsql":
					{
						pipeline = new PostgreSqlEntityPipeline();
						break;
					}
				case "MySql.Data.MySqlClient":
					{
						pipeline = new MySqlEntityPipeline();
						break;
					}
				case "System.Data.SqlClient":
					{
						pipeline = new SqlServerEntityPipeline();
						break;
					}
				case "MongoDB":
					{
						pipeline = new MongoDbEntityPipeline(Core.Environment.DataConnectionString);
						break;
					}
				default:
					{
						pipeline = new NullPipeline();
						break;
					}
			}
			return pipeline;
		}

		protected override void PreInitComponent(params string[] arguments)
		{
			base.PreInitComponent(arguments);

			if (arguments.Contains("skip"))
			{
				return;
			}

			if (Entities == null || Entities.Count == 0)
			{
				throw new SpiderException("Count of entity is zero.");
			}

			foreach (var entity in Entities)
			{
				foreach (var pipeline in Pipelines)
				{
					BaseEntityPipeline newPipeline = pipeline as BaseEntityPipeline;
					newPipeline?.AddEntity(entity);
				}
			}

			if (IfRequireInitStartRequests(arguments) && StartUrlBuilders != null && StartUrlBuilders.Count > 0)
			{
				for (int i = 0; i < StartUrlBuilders.Count; ++i)
				{
					var builder = StartUrlBuilders[i];
					Logger.MyLog(Identity, $"[{i + 1}] Add extra start urls to scheduler.", LogLevel.Info);
					builder.Build(Site);
				}
			}
		}

		private static void ValidateEntityDefine(EntityDefine entity)
		{
			var columns = GetColumns(entity);

			if (columns.Count == 0)
			{
				throw new SpiderException($"Columns is necessary for {entity.Name}.");
			}
			if (entity.TableInfo == null)
			{
				return;
			}
			if (!string.IsNullOrEmpty(entity.TableInfo.Primary))
			{
				if (entity.TableInfo.Primary != Core.Environment.IdColumn)
				{
					var items = new HashSet<string>(entity.TableInfo.Primary.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries).Select(c => c.Trim()));
					if (items.Count > 0)
					{
						foreach (var item in items)
						{
							var column = columns.FirstOrDefault(c => c.Name == item);
							if (column == null)
							{
								throw new SpiderException("Columns set as primary is not a property of your entity.");
							}
							if (column.DataType == DataTypeNames.String && (column.Length > 256 || column.Length <= 0))
							{
								throw new SpiderException("Column length of primary should not large than 256.");
							}
							column.NotNull = true;
						}
					}
					else
					{
						entity.TableInfo.Primary = Core.Environment.IdColumn;
					}
				}
			}
			else
			{
				entity.TableInfo.Primary = Core.Environment.IdColumn;
			}

			if (entity.TableInfo.UpdateColumns != null && entity.TableInfo.UpdateColumns.Length > 0)
			{
				foreach (var column in entity.TableInfo.UpdateColumns)
				{
					if (columns.All(c => c.Name != column))
					{
						throw new SpiderException("Columns set as update is not a property of your entity.");
					}
				}
				var updateColumns = new List<string>(entity.TableInfo.UpdateColumns);
				updateColumns.Remove(entity.TableInfo.Primary);

				entity.TableInfo.UpdateColumns = updateColumns.ToArray();

				if (entity.TableInfo.UpdateColumns.Length == 0)
				{
					throw new SpiderException("There is no column need update.");
				}
			}

			if (entity.TableInfo.Indexs != null && entity.TableInfo.Indexs.Length > 0)
			{
				for (int i = 0; i < entity.TableInfo.Indexs.Length; ++i)
				{
					var items = new HashSet<string>(entity.TableInfo.Indexs[i].Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries).Select(c => c.Trim()));

					if (items.Count == 0)
					{
						throw new SpiderException("Index should contain more than a column.");
					}
					if (items.Count == 1 && items.First() == entity.TableInfo.Primary)
					{
						throw new SpiderException("Primary is no need to create another index.");
					}
					foreach (var item in items)
					{
						var column = columns.FirstOrDefault(c => c.Name == item);
						if (column == null)
						{
							throw new SpiderException("Columns set as index is not a property of your entity.");
						}
						if (column.DataType == DataTypeNames.String && (column.Length <= 0 || column.Length > 256))
						{
							throw new SpiderException("Column length of index should not large than 256.");
						}
					}
					entity.TableInfo.Indexs[i] = string.Join(",", items);
				}
			}
			if (entity.TableInfo.Uniques != null && entity.TableInfo.Uniques.Length > 0)
			{
				for (int i = 0; i < entity.TableInfo.Uniques.Length; ++i)
				{
					var items = new HashSet<string>(entity.TableInfo.Uniques[i].Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries).Select(c => c.Trim()));

					if (items.Count == 0)
					{
						throw new SpiderException("Unique should contain more than a column.");
					}
					if (items.Count == 1 && items.First() == entity.TableInfo.Primary)
					{
						throw new SpiderException("Primary is no need to create another unique.");
					}
					foreach (var item in items)
					{
						var column = columns.FirstOrDefault(c => c.Name == item);
						if (column == null)
						{
							throw new SpiderException("Columns set as unique is not a property of your entity.");
						}
						if (column.DataType == DataTypeNames.String && (column.Length <= 0 || column.Length > 256))
						{
							throw new SpiderException("Column length of unique should not large than 256.");
						}
					}
					entity.TableInfo.Uniques[i] = string.Join(",", items);
				}
			}
		}

		private static List<Column> GetColumns(EntityDefine entity)
		{
			var columns = new List<Column>();
			foreach (var f in entity.Columns)
			{
				var column = f;
				if (!column.IgnoreStore)
				{
					columns.Add(column);
				}
			}
			return columns;
		}
	}
}
