using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using NHibernate.Collection;
using NHibernate.Engine;
using NHibernate.Proxy;
using NHibernate.Persister.Collection;

namespace NHibernate.CollectionQuery
{
    using Tuple = System.Tuple;
    using Type = System.Type;
    using SessionTypeAndOwnerType = System.Tuple<System.Type, System.Type>;
    using OwnerTypeAndElementType = System.Tuple<System.Type, System.Type>;
    
    internal static class CollectionQueryableExtensions
    {
        private static readonly IInternalLogger log = LoggerProvider.LoggerFor(typeof(CollectionQueryableExtensions));

        private delegate IQueryable SessionQueryGetter(object session);
        private delegate IQueryable SelectMany(IQueryable ownerQueryable, Expression collectionSelection);
        private delegate ISessionImplementor SessionGetter(IPersistentCollection persistentCollection);
        private delegate IQueryable QueryFactory(IPersistentCollection persistentCollection, ISessionImplementor session);

        private static readonly MethodInfo createQueryFactoryMethod;
        private static readonly SessionGetter sessionGetter;
        private static readonly ConcurrentDictionary<SessionTypeAndOwnerType, SessionQueryGetter> sessionQueryGetterCache;
        private static readonly ConcurrentDictionary<OwnerTypeAndElementType, SelectMany> selectManyCache;
        private static readonly ConcurrentDictionary<string, QueryFactory> roleQueryFactoryCache;

        static CollectionQueryableExtensions()
        {
            createQueryFactoryMethod = typeof(CollectionQueryableExtensions).GetMethod("CreateQueryFactory", BindingFlags.NonPublic | BindingFlags.Static);
            sessionGetter = CreateSessionGetter();
            sessionQueryGetterCache = new ConcurrentDictionary<SessionTypeAndOwnerType, SessionQueryGetter>();
            selectManyCache = new ConcurrentDictionary<OwnerTypeAndElementType, SelectMany>();
            roleQueryFactoryCache = new ConcurrentDictionary<string, QueryFactory>();
        }

        public static IQueryable<T> Query<T>(this ICollection<T> source, ISessionImplementor session = null)
        {
            var persistentCollection = source as IPersistentCollection;
            if (persistentCollection == null || persistentCollection.WasInitialized)
            {
                return source.AsQueryable();
            }

            if (session == null)
            {
                session = sessionGetter(persistentCollection);
            }

            // Capture *outside* the closure so that a reference to the
            // persistent collection is not captured.
            var ownerType = GetOwnerType(persistentCollection);
            var elementType = typeof(T);
            var sessionFactory = session.Factory;

            // Cache by role because the collection will not change.
            var queryFactory = roleQueryFactoryCache.GetOrAdd(persistentCollection.Role, collectionRole =>
            {
                var method = createQueryFactoryMethod.MakeGenericMethod(ownerType, elementType);
                var result = method.Invoke(null, new object[] { collectionRole, sessionFactory });
                return (QueryFactory)result;
            });

            var query = queryFactory(persistentCollection, session);
            return (IQueryable<T>)query;
        }

        private static QueryFactory CreateQueryFactory<TOwner, TElement>(string collectionRole, ISessionFactoryImplementor sessionFactory)
        {
            string oneToManyKeyPropertyName;
            if (CanCreateSimpleOneToManyQuery(collectionRole, sessionFactory, out oneToManyKeyPropertyName))
            {
                log.InfoFormat("Simple One-to-Many Querable factory created for \"{0}\" collection.", collectionRole);

                return (persistentCollection, session) =>
                    CreateSimpleOneToManyQuery<TOwner, TElement>(persistentCollection, session, oneToManyKeyPropertyName);
            }
            else
            {
                log.InfoFormat("SelectMany Queryable factory created for \"{0}\" collection.", collectionRole);

                return (persistentCollection, session) =>
                    CreateSelectManyQuery<TOwner, TElement>(persistentCollection, session);
            }
        }

        private static bool CanCreateSimpleOneToManyQuery(string collectionRole, ISessionFactoryImplementor sessionFactory, out string keyPropertyName)
        {
            keyPropertyName = null;

            var collectionMetadata = sessionFactory.GetCollectionMetadata(collectionRole) as AbstractCollectionPersister;

            // Ignore custom "where" because we build a specific expression tree and
            // need to let the lower-layers of NHibernate handle it. If you're going
            // this route, you probably shouldn't be using queryable collections.
            if (collectionMetadata == null || !collectionMetadata.IsOneToMany || collectionMetadata.HasWhere)
            {
                return false;
            }

            // Although it's called "KeyColumnNames", they actually represent
            // the name of the properties (or fields) that are eventually mapped to
            // columns.
            var keyColumnNames = collectionMetadata.KeyColumnNames;

            if (keyColumnNames == null || keyColumnNames.Length != 1)
            {
                return false;
            }

            // If the element doesn't have a reference to the parent, there's no
            // easy way to make a LINQ query with the required field.
            var elementHasReferenceToParent = !collectionMetadata.ElementPersister.PropertyNames.Contains(keyColumnNames[0]);
            if (elementHasReferenceToParent)
            {
                return false;
            }
            
            keyPropertyName = keyColumnNames[0];

            return true;
        }

        private static IQueryable CreateSimpleOneToManyQuery<TOwner, TElement>(IPersistentCollection persistentCollection, ISessionImplementor session, string keyPropertyName)
        {
            var elementType = typeof(TElement);
            var elementParameter = Expression.Parameter(elementType);
            var ownerType = typeof(TOwner);

            // The "x => x.Owner == owner" predicate.
            var predicate = (Expression<Func<TElement, bool>>)Expression.Lambda(
                Expression.Equal(
                    Expression.PropertyOrField(elementParameter, keyPropertyName),   
                    Expression.Constant(persistentCollection.Owner, ownerType)
                ),
                elementParameter
             );

            var sessionType = GetSessionType(session);

            // Setup the "Query<TElement>".
            var sessionQueryGetter = sessionQueryGetterCache.GetOrAdd(new SessionTypeAndOwnerType(sessionType, elementType), CreateSessionQueryGetter);
            var elementsQuery = (IQueryable<TElement>)sessionQueryGetter(session);

            // Assemble the final query:
            //   Query<TElement>().Where(x => x.Owner == owner)
            var filteredQuery = Queryable.Where(elementsQuery, predicate);
            return filteredQuery;
        }

        private static IQueryable CreateSelectManyQuery<TOwner, TElement>(IPersistentCollection persistentCollection, ISessionImplementor session)
        {
            var elementType = typeof(TElement);
            var sessionType = GetSessionType(session);
            var ownerType = GetOwnerType(persistentCollection);
            var collectionPropertyName = persistentCollection.Role.Split('.').Last();

            // Setup the "Query<TOwner>".
            var sessionQueryGetter = sessionQueryGetterCache.GetOrAdd(new SessionTypeAndOwnerType(sessionType, ownerType), CreateSessionQueryGetter);
            var ownerQueryable = (IQueryable<TOwner>)sessionQueryGetter(session);

            var ownerParameter = Expression.Parameter(ownerType);

            // The "x => x == owner" to ensure only the collection's
            // owner is selected.
            var predicate = (Expression<Func<TOwner, bool>>)Expression.Lambda(
                Expression.Equal(ownerParameter,
                    Expression.Constant(
                        persistentCollection.Owner,
                        ownerType
                    )
                ),
                ownerParameter
            );
            // The "x => x.Collection" selector to for the "SelectMany".
            var collectionSelector = Expression.Lambda(
                GetCollectionSelectorType(ownerType, elementType),
                Expression.Property(ownerParameter, collectionPropertyName),
                ownerParameter
            );

            // Assemble the final query:
            //   Query<TOwner>().Where(x => x == owner).SelectMany(x => x.Collection)
            var ownerQuery = Queryable.Where(ownerQueryable, predicate);
            var selectMany = selectManyCache.GetOrAdd(new OwnerTypeAndElementType(ownerType, elementType), CreateSelectMany);
            var elementsQuery = selectMany(ownerQuery, collectionSelector);
            return elementsQuery;
        }

        private static SessionGetter CreateSessionGetter()
        {
            var sessionProperty = typeof(AbstractPersistentCollection)
                .GetProperty("Session", BindingFlags.Instance | BindingFlags.NonPublic);

            var collectionParameter = Expression.Parameter(typeof(IPersistentCollection));

            var body = Expression.Property(
                Expression.Convert(collectionParameter, typeof(AbstractPersistentCollection)),
                sessionProperty
            );

            return Expression.Lambda<SessionGetter>(body, collectionParameter)
                .Compile();
        }

        private static SessionQueryGetter CreateSessionQueryGetter(SessionTypeAndOwnerType types)
        {
            var sessionType = types.Item1;
            var ownerType = types.Item2;

            var queryMethod = typeof(NHibernate.Linq.LinqExtensionMethods)
                .GetMethod("Query", new[] { sessionType })
                .MakeGenericMethod(ownerType);

            // Build the "Func<TSession, IQueryable<TElement>>" factory method
            // where TSession is ISession or IStatelessSession.
            var sessionParameter = Expression.Parameter(typeof(object));
            var body = Expression.Call(
                null, 
                queryMethod,
                Expression.Convert(sessionParameter, sessionType)
            );
            return Expression.Lambda<SessionQueryGetter>(body, sessionParameter)
                .Compile();
        }

        private static SelectMany CreateSelectMany(OwnerTypeAndElementType types)
        {
            var ownerType = types.Item1;
            var itemType = types.Item2;

            var selectManyMethod = typeof(Queryable).GetMethods()
                .First(m =>
                {
                    var parameters = m.GetParameters();
                    if (m.Name != "SelectMany" || parameters.Length != 2) return false;

                    var p1 = parameters[1].ParameterType;

                    return p1.GetGenericTypeDefinition() == typeof(Expression<>)
                        && p1.GetGenericArguments()[0].GetGenericTypeDefinition() == typeof(Func<,>);
                })
                .MakeGenericMethod(ownerType, itemType);

            var ownerQueryableParameter = Expression.Parameter(typeof(object));
            var collectionSelectorParameter = Expression.Parameter(typeof(object));

            // Build the type "Expression<Func<TOwner, IEnumerable<TItem>>"
            var selectorType = typeof(Expression<>).MakeGenericType(
                GetCollectionSelectorType(ownerType, itemType)
            );
            var body = Expression.Call(
                null,
                selectManyMethod,
                Expression.Convert(ownerQueryableParameter, typeof(IQueryable<>).MakeGenericType(ownerType)),
                Expression.Convert(collectionSelectorParameter, selectorType)
            );
            return Expression.Lambda<SelectMany>(body, ownerQueryableParameter, collectionSelectorParameter)
                .Compile();
        }

        private static Type GetSessionType(ISessionImplementor session)
        {
            if (session is ISession)
            {
                return typeof(ISession);
            }
            else
            {
                return typeof(IStatelessSession);
            }
        }

        private static Type GetOwnerType(IPersistentCollection persistentCollection)
        {
            var ownerProxy = persistentCollection.Owner as INHibernateProxy;

            if (ownerProxy == null)
            {
                return persistentCollection.Owner.GetType();
            }
            else
            {
                return ownerProxy.HibernateLazyInitializer.PersistentClass;
            }
        }

        private static Type GetCollectionSelectorType(Type ownerType, Type itemType)
        {
            // Build the type "Func<TOwner, IEnumerable<TItem>"
            return typeof(Func<,>)
                .MakeGenericType(
                    ownerType,
                    typeof(IEnumerable<>).MakeGenericType(itemType)
                );
        }
    }
}