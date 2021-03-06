﻿using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Xml.Linq;
using Examine.Test.PartialTrust;
using Lucene.Net.Analysis.Standard;
using Lucene.Net.Documents;
using Lucene.Net.Store;
using NUnit.Framework;

using Lucene.Net.Search;
using Lucene.Net.Index;
using System.Diagnostics;
using Examine.LuceneEngine;
using Examine.LuceneEngine.Providers;
using System.Threading;
using Examine.Test.DataServices;
using UmbracoExamine;

namespace Examine.Test.Index
{

    /// <summary>
    /// Tests the standard indexing capabilities
    /// </summary>
    [TestFixture, RequiresSTA]
	public class IndexTest : AbstractPartialTrustFixture<IndexTest>
    {

        //[Test]
        //public void Add_Sortable_Field_Api()
        //{
        //    _indexer.DocumentWriting += ExternalIndexer_DocumentWriting;
        //    _indexer.RebuildIndex();
        //}

        //void ExternalIndexer_DocumentWriting(object sender, DocumentWritingEventArgs e)
        //{
        //    var field = e.Document.GetFieldable("writerName");
        //    if (field != null)
        //    {
        //        var sortedField = new Field(
        //            LuceneIndexer.SortedFieldNamePrefix + field.Name(),
        //            field.StringValue(),
        //            Field.Store.NO, //we don't want to store the field because we're only using it to sort, not return data
        //            Field.Index.NOT_ANALYZED,
        //            Field.TermVector.NO);
        //        e.Document.Add(sortedField);
        //    }
        //}

        /// <summary>
        /// This will create a new index queue item for the same ID multiple times to ensure that the 
        /// index does not end up with duplicate entries.
        /// </summary>
        [Test]
        public void Index_Ensure_No_Duplicates_In_Async()
        {

	        using (var d = new RAMDirectory())
	        {
				var customIndexer = IndexInitializer.GetUmbracoIndexer(d);

				var isIndexing = false;

				EventHandler operationComplete = (sender, e) =>
				{
					isIndexing = false;
				};

				//add the handler for optimized since we know it will be optimized last based on the commit count
				customIndexer.IndexOperationComplete += operationComplete;

				//remove the normal indexing error handler
				customIndexer.IndexingError -= IndexInitializer.IndexingError;

				//run in async mode
				customIndexer.RunAsync = true;

				//get a node from the data repo
				var node = _contentService.GetPublishedContentByXPath("//*[string-length(@id)>0 and number(@id)>0]")
					.Root
					.Elements()
					.First();

				//get the id for th node we're re-indexing.
				var id = (int)node.Attribute("id");

				//set our internal monitoring flag
				isIndexing = true;

				//reindex the same node a bunch of times
				for (var i = 0; i < 29; i++)
				{
					customIndexer.ReIndexNode(node, IndexTypes.Content);
				}

				//we need to check if the indexing is complete
				while (isIndexing)
				{
					//wait until indexing is done
					Thread.Sleep(1000);
				}

				//reset the async mode and remove event handler
				customIndexer.IndexOptimized -= operationComplete;
				customIndexer.IndexingError += IndexInitializer.IndexingError;
				customIndexer.RunAsync = false;


				//ensure no duplicates
				Thread.Sleep(10000); //seems to take a while to get its shit together... this i'm not sure why since the optimization should have def finished (and i've stepped through that code!)
				var customSearcher = IndexInitializer.GetLuceneSearcher(d);
				var results = customSearcher.Search(customSearcher.CreateSearchCriteria().Id(id).Compile());
				Assert.AreEqual(1, results.Count());                
	        }            
        }

        [Test]
        public void Index_Ensure_No_Duplicates_In_Non_Async()
        {
            //set to 5 so optmization occurs frequently
            _indexer.OptimizationCommitThreshold = 5;

            //get a node from the data repo
            var node = _contentService.GetPublishedContentByXPath("//*[string-length(@id)>0 and number(@id)>0]")
                .Root
                .Elements()
                .First();

            //get the id for th node we're re-indexing.
            var id = (int)node.Attribute("id");

            //reindex the same node a bunch of times
            for (var i = 0; i < 29; i++)
            {
                _indexer.ReIndexNode(node, IndexTypes.Content);
            }
           
            //ensure no duplicates
            var results = _searcher.Search(_searcher.CreateSearchCriteria().Id(id).Compile());
            Assert.AreEqual(1, results.Count());
        }     

        /// <summary>
        /// This test makes sure that .del files get processed before .add files
        /// </summary>
        [Test]
        public void Index_Ensure_Queue_File_Ordering()
        {
            _indexer.RebuildIndex();

            var isDeleted = false;
            var isAdded = false;

            //first we need to wire up some events... we need to ensure that during an update process that the item is deleted before it's added

            EventHandler<DeleteIndexEventArgs> indexDeletedHandler = (sender, e) =>
            {
                isDeleted = true;
                Assert.IsFalse(isAdded, "node was added before it was deleted!");
            };

            //add index deleted event handler
            _indexer.IndexDeleted += indexDeletedHandler;

            EventHandler<IndexedNodeEventArgs> nodeIndexedHandler = (sender, e) =>
            {
                isAdded = true;
                Assert.IsTrue(isDeleted, "node was not deleted first!");
            };

            //add index added event handler
            _indexer.NodeIndexed += nodeIndexedHandler;
            //get a node from the data repo
            var node = _contentService.GetPublishedContentByXPath("//*[string-length(@id)>0 and number(@id)>0]")
                .Root
                .Elements()
                .First();

            //this will do the reindex (deleting, then updating)
            _indexer.ReIndexNode(node, IndexTypes.Content);

            _indexer.IndexDeleted -= indexDeletedHandler;
            _indexer.NodeIndexed -= nodeIndexedHandler;

            Assert.IsTrue(isDeleted, "node was not deleted");
            Assert.IsTrue(isAdded, "node was not re-added");
        }


         


        [Test]
        public void Index_Rebuild_Index()
        {

            //get searcher and reader to get stats
            var r = ((IndexSearcher)_searcher.GetSearcher()).GetIndexReader();   
                                    
            //there's 16 fields in the index, but 3 sorted fields
            var fields = r.GetFieldNames(IndexReader.FieldOption.ALL);

            Assert.AreEqual(21, fields.Count());
            //ensure there's 3 sorting fields
            Assert.AreEqual(4, fields.Count(x => x.StartsWith(LuceneIndexer.SortedFieldNamePrefix)));
            //there should be 11 documents (10 content, 1 media)
            Assert.AreEqual(10, r.NumDocs());

            //test for the special fields to ensure they are there:
            Assert.AreEqual(1, fields.Where(x => x == LuceneIndexer.IndexNodeIdFieldName).Count());
            Assert.AreEqual(1, fields.Where(x => x == LuceneIndexer.IndexTypeFieldName).Count());
            Assert.AreEqual(1, fields.Where(x => x == UmbracoContentIndexer.IndexPathFieldName).Count());
            Assert.AreEqual(1, fields.Where(x => x == UmbracoContentIndexer.NodeTypeAliasFieldName).Count());

        }


        
        #region Private methods and properties

        private readonly TestContentService _contentService = new TestContentService();
        private readonly TestMediaService _mediaService = new TestMediaService();

        private static UmbracoExamineSearcher _searcher;
        private static UmbracoContentIndexer _indexer;

        #endregion

        #region Initialize and Cleanup

	    private Lucene.Net.Store.Directory _luceneDir;

	    public override void TestTearDown()
        {
            //set back to 100
            _indexer.OptimizationCommitThreshold = 100;
			_luceneDir.Dispose();
        }

		public override void TestSetup()
        {
			_luceneDir = new RAMDirectory();
			_indexer = IndexInitializer.GetUmbracoIndexer(_luceneDir);
            _indexer.RebuildIndex();
			_searcher = IndexInitializer.GetUmbracoSearcher(_luceneDir);
        }


        #endregion
    }
}
