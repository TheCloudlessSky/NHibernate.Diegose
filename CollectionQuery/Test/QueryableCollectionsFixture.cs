using System;
using System.Collections.Generic;
using System.Linq;
using System.Xml.Serialization;
using NHibernate.Cfg;
using NHibernate.Cfg.MappingSchema;
using NHibernate.CollectionQuery.Test.Domain;
using NHibernate.Dialect;
using NHibernate.Mapping.ByCode;
using NHibernate.Tool.hbm2ddl;
using NHibernate.Linq;
using NUnit.Framework;
using NHibernate.Type;
using System.Linq.Expressions;

namespace NHibernate.CollectionQuery.Test
{
    [TestFixture]
    public abstract class QueryableCollectionFixture
    {
        private Configuration configuration;
        protected ISessionFactory SessionFactory { get; private set; }
        protected object Id { get; private set; }

        [TestFixtureSetUp]
        public void TestFixtureSetUp()
        {
            configuration = new Configuration();
            configuration.SessionFactory()
                         .Integrate.Using<SQLiteDialect>()
                         .Connected.Using("Data source=testdb")
                         .AutoQuoteKeywords()
                         .LogSqlInConsole()
                         .EnableLogFormattedSql();

            var mapper = new ModelMapper();
            SetupMapping(mapper);

            var mappingDocument = mapper.CompileMappingForAllExplicitlyAddedEntities();
            new XmlSerializer(typeof(HbmMapping)).Serialize(Console.Out, mappingDocument);
            configuration.AddDeserializedMapping(mappingDocument, "Mappings");
            new SchemaExport(configuration).Create(true, true);
            SessionFactory = configuration.BuildSessionFactory();

            using (var session = SessionFactory.OpenSession())
            using (var tx = session.BeginTransaction())
            {
                var foo = new Foo { Bars = CreateCollection() };
                foo.Bars.Add(new Bar { Data = 1, ParentFoo = foo });
                foo.Bars.Add(new Bar { Data = 2, ParentFoo = foo });
                Id = session.Save(foo);
                tx.Commit();
            }

            SessionFactory.Statistics.IsStatisticsEnabled = true;
        }

        protected abstract ICollection<Bar> CreateCollection();

        private void SetupMapping(ModelMapper mapper)
        {
            mapper.Class<Foo>(cm =>
            {
                cm.Id(x => x.Id, im =>
                {
                    im.Type(new Int32Type());
                    im.Generator(Generators.Identity);
                });
                CustomizeFooMapper(cm);
            });
            mapper.Class<Bar>(cm =>
            {
                cm.Id(x => x.Id, im =>
                {
                    im.Type(new Int32Type());
                    im.Generator(Generators.Identity);
                });
                cm.Property(x => x.Data, pm =>
                {
                    pm.Type(new Int32Type());
                });
                CustomizeBarMapper(cm);
            });
        }

        protected abstract void CustomizeFooMapper(IClassMapper<Foo> mapper);

        protected virtual void CustomizeBarMapper(IClassMapper<Bar> mapper)
        {

        }

        [TestFixtureTearDown]
        public void TestFixtureTearDown()
        {
            new SchemaExport(configuration).Drop(false, true);
        }

        [SetUp]
        public void Setup()
        {
            // Because we run tests for the same collection many-to-many vs one-to-many we
            // need a clean cache.
            CollectionQueryable.ClearCache();

            SessionFactory.Statistics.Clear();
        }

        void QueryWithoutInitializing(Action<ICollection<Bar>, Bar> verify)
        {
            using (var session = SessionFactory.OpenSession())
            {
                var foo = session.Get<Foo>(Id);
                var bar = foo.Bars.AsQueryable().Single(b => b.Data == 2);
                verify(foo.Bars, bar);
            }
        }

        void QueryInitialized(Action<ICollection<Bar>, Bar> verify)
        {
            using (var session = SessionFactory.OpenSession())
            {
                var foo = session.Get<Foo>(Id);
                NHibernateUtil.Initialize(foo.Bars);
                var bar = foo.Bars.AsQueryable().Single(b => b.Data == 2);
                verify(foo.Bars, bar);
            }
        }

        [Test]
        public void LinqQueriesDontCauseInitialization()
        {
            QueryWithoutInitializing((bars, bar) =>
                                     Assert.False(NHibernateUtil.IsInitialized(bars), "collection was initialized"));
        }

        [Test]
        public void LinqQueriesDontLoadAdditionalEntities()
        {
            QueryWithoutInitializing((bars, bar) =>
                                     Assert.AreEqual(2, SessionFactory.Statistics.EntityLoadCount,
                                                     "unexpected numer of entities loaded"));
        }

        [Test]
        public void LinqQueriesOnInitializedCollectionsReturnTheRightElement()
        {
            QueryInitialized((bars, bar) =>
                             {
                                 Assert.NotNull(bar, "could not retrieve collection element");
                                 Assert.AreEqual(2, bar.Data, "invalid element retrieved");
                             });
        }

        [Test]
        public void LinqQueriesOnUninitializedCollectionsReturnTheRightElement()
        {
            QueryWithoutInitializing((bars, bar) =>
                                     {
                                         Assert.NotNull(bar, "could not retrieve collection element");
                                         Assert.AreEqual(2, bar.Data, "invalid element retrieved");
                                     });
        }

        [Test]
        public void AlreadyInitializedCollectionsAreQueriedInMemory()
        {
            QueryInitialized((bars, bar) =>
                             Assert.AreEqual(0, SessionFactory.Statistics.QueryExecutionCount,
                                             "unexpected query execution"));
        }

        [Test]
        public void UsingAsQueryableExtensionMethod()
        {
            using (var session = SessionFactory.OpenSession())
            {
                var foo = session.Get<Foo>(Id);
                var bar = foo.Bars.AsQueryable().SingleOrDefault(b => b.Data == 2);
                Assert.AreEqual(2, bar.Data, "invalid element retrieved");
            }
        }

        [Test]
        public void PreventSelectingWrongSelectManyQueryableMethod()
        {
            using (var session = SessionFactory.OpenSession())
            {
                var foo = session.Query<Foo>().FirstOrDefault();
                var bar = foo.Bars.AsQueryable().Where(b => b.Data != 0).FirstOrDefault();
                Assert.IsNotNull(bar, "no element retrieved");
            }
        }

        [Test]
        public virtual void BuildsTheCorrectExpression()
        {
            using (var session = SessionFactory.OpenSession())
            {
                var foo = session.Get<Foo>(Id);

                var actualExpression = foo.Bars.AsQueryable().Expression;

                var fooParameter = Expression.Parameter(typeof(Foo));
                var wherePredicate = (Expression<Func<Foo, bool>>)Expression.Lambda(
                    Expression.Equal(
                        fooParameter,
                        Expression.Constant(foo)
                    ),
                    fooParameter
                );
                var expectedExpression = session.Query<Foo>()
                    .Where(wherePredicate)
                    .SelectMany(Param_0 => Param_0.Bars)
                    .Expression;
                Assert.AreEqual(expectedExpression.ToString(), actualExpression.ToString());
            }
        }
    }

    public abstract class QueryableCollectionSimpleOneToManyFixture : QueryableCollectionFixture
    {
        protected abstract override ICollection<Bar> CreateCollection();

        protected abstract override void CustomizeFooMapper(IClassMapper<Foo> mapper);

        [Test]
        public override void BuildsTheCorrectExpression()
        {
            using (var session = SessionFactory.OpenSession())
            {
                var foo = session.Get<Foo>(Id);
                
                var actualExpression = foo.Bars.AsQueryable().Expression;

                var barParameter = Expression.Parameter(typeof(Bar));
                var wherePredicate = (Expression<Func<Bar, bool>>)Expression.Lambda(
                    Expression.Equal(
                        Expression.Property(barParameter, "ParentFoo"),
                        Expression.Constant(foo)
                    ),
                    barParameter
                );
                var expectedExpression = session.Query<Bar>()
                    .Where(wherePredicate)
                    .Expression;
                Assert.AreEqual(expectedExpression.ToString(), actualExpression.ToString());
            }
        }

        protected override void CustomizeBarMapper(IClassMapper<Bar> mapper)
        {
            mapper.ManyToOne(x => x.ParentFoo, mom =>
            {
                // Use a different column to ensure that the property name is mapped
                // in the predicate.
                mom.Column("Parent_Foo_Id");
            });
        }
    }
}