namespace NHibernate.CollectionQuery.Test.Domain
{
    public class Bar
    {
        public virtual int Id { get; set; }
        public virtual int Data { get; set; }

        // Ignored when not doing a simple OneToMany test.
        public virtual Foo ParentFoo { get; set; }
    }
}