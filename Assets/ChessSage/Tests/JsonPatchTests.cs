using System;
using ChessSage.Core.Json;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace ChessSage.Tests
{
    /// <summary>JSON Patch（RFC 6902）等价性测试，对照 json_patch_utils.py 的纯 Python 分支。</summary>
    public sealed class JsonPatchTests
    {
        [Test]
        public void ApplyMatchesPythonGolden()
        {
            var golden = TestEnv.LoadFixture("jsonpatch_golden");
            foreach (var t in (JArray)golden["cases"])
            {
                var c = (JObject)t;
                var name = c["name"].Value<string>();
                var doc = c["doc"];
                var patch = (JArray)c["patch"];
                var expected = c["result"];
                var error = c["error"]?.Value<string>();

                if (!string.IsNullOrEmpty(error))
                {
                    Assert.Catch<Exception>(() => JsonPatch.Apply(doc, patch), $"{name}: expected error");
                    continue;
                }

                var actual = JsonPatch.Apply(doc, patch);
                Assert.IsTrue(JToken.DeepEquals(expected, actual), $"{name}: result mismatch\n{actual}\n!=\n{expected}");

                if (c["is_diff"]?.Value<bool>() == true)
                {
                    var regenerated = JsonPatch.GenerateDiff(doc, (JToken)c["modified"]);
                    var roundtrip = JsonPatch.Apply(doc, regenerated);
                    Assert.IsTrue(JToken.DeepEquals(c["modified"], roundtrip), $"{name}: diff roundtrip");
                }
            }
        }

        [Test]
        public void MoveIntoOwnChildIsRejected()
        {
            var doc = JObject.Parse("{\"a\":{\"b\":1}}");
            var patch = JArray.Parse("[{\"op\":\"move\",\"from\":\"/a\",\"path\":\"/a/b\"}]");
            Assert.Throws<ArgumentException>(() => JsonPatch.Apply(doc, patch));
        }

        [Test]
        public void IsValidRejectsUnknownOp()
        {
            var patch = JArray.Parse("[{\"op\":\"frobnicate\",\"path\":\"/a\"}]");
            var (ok, _) = JsonPatch.IsValid(patch);
            Assert.IsFalse(ok);
        }
    }
}