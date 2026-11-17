using Topo.Core;
using Topo.Training;
using Xunit;

namespace Topo.Training.Tests;

public sealed class TokenReadoutLearningTests
{
    [Fact]
    public void Preprocessor_FitsTrainingOnly_DropsConstantsAndZScoresRetainedColumns()
    {
        var training = new[]
        {
            Example("a", GeoCoordinateToken.North, 1, 9, 3),
            Example("b", GeoCoordinateToken.South, 3, 9, 3),
        };

        var preprocessor = TokenReadoutPreprocessor.Fit(training);

        Assert.Equal(3, preprocessor.OriginalFeatureDimension);
        Assert.Equal(new[] { 0 }, preprocessor.RetainedFeatureIndices);
        Assert.Equal(new[] { -1d }, preprocessor.Transform(training[0].Vector));
        Assert.Equal(new[] { 1d }, preprocessor.Transform(training[1].Vector));
        Assert.Equal(new[] { 17d }, preprocessor.Transform(Example("test", GeoCoordinateToken.North, 19, 0, 0).Vector));
    }

    [Fact]
    public void CoordinateSplit_IsDeterministicAndNeverLeaksACoordinateAcrossPartitions()
    {
        var examples = Enumerable.Range(0, 20)
            .SelectMany(index => new[]
            {
                Example($"coordinate-{index:D2}", GeoCoordinateToken.North, index, 0, 0),
                Example($"coordinate-{index:D2}", GeoCoordinateToken.South, index, 1, 0),
            })
            .ToArray();

        var first = TokenReadoutDatasetSplitter.SplitByCoordinate(examples, 77);
        var second = TokenReadoutDatasetSplitter.SplitByCoordinate(examples, 77);

        Assert.Equal(14, first.TrainingCoordinateIds.Count);
        Assert.Equal(3, first.ValidationCoordinateIds.Count);
        Assert.Equal(3, first.TestCoordinateIds.Count);
        Assert.Equal(first.TrainingCoordinateIds, second.TrainingCoordinateIds);
        Assert.Empty(first.TrainingCoordinateIds.Intersect(first.ValidationCoordinateIds));
        Assert.Empty(first.TrainingCoordinateIds.Intersect(first.TestCoordinateIds));
        Assert.Empty(first.ValidationCoordinateIds.Intersect(first.TestCoordinateIds));
        Assert.Equal(28, first.Training.Count);
        Assert.Equal(6, first.Validation.Count);
        Assert.Equal(6, first.Test.Count);
    }

    [Fact]
    public void SoftmaxClassifier_LearnsASeparableMulticlassDatasetDeterministically()
    {
        var labels = new[] { GeoCoordinateToken.North, GeoCoordinateToken.South, GeoCoordinateToken.East };
        var examples = new[]
        {
            Prepared("a", GeoCoordinateToken.North, 2, 0), Prepared("b", GeoCoordinateToken.North, 3, .1),
            Prepared("c", GeoCoordinateToken.South, 0, 2), Prepared("d", GeoCoordinateToken.South, .1, 3),
            Prepared("e", GeoCoordinateToken.East, -2, -2), Prepared("f", GeoCoordinateToken.East, -3, -2.5),
        };
        var parameters = new TokenReadoutTrainingParameters(123, .1, 600, 1e-4);
        var first = new TokenSoftmaxClassifier(labels, 2, parameters.Seed);
        var second = new TokenSoftmaxClassifier(labels, 2, parameters.Seed);

        first.Train(examples, parameters);
        second.Train(examples, parameters);

        Assert.Equal(1d, TokenReadoutEvaluator.Evaluate(first, examples).Accuracy);
        Assert.Equal(first.Weights.SelectMany(row => row), second.Weights.SelectMany(row => row));
        Assert.Equal(first.Biases, second.Biases);
    }

    [Fact]
    public void ModelStore_RoundTripsClassifierAndPreprocessingMetadata()
    {
        var examples = new[] { Example("a", GeoCoordinateToken.North, 1, 2, 3), Example("b", GeoCoordinateToken.South, 3, 2, 3) };
        var preprocessor = TokenReadoutPreprocessor.Fit(examples);
        var classifier = new TokenSoftmaxClassifier(Enum.GetValues<GeoCoordinateToken>(), preprocessor.FeatureDimension, 9);
        var parameters = new TokenReadoutTrainingParameters(9, .1, 2, 0);
        var population = new ReadoutPopulationConfiguration(1, "reference", 22, new[] { 720575940602977248L }, NeuralFeatureExtractor.FeatureFields);
        var path = Path.Combine(Path.GetTempPath(), $"topo-token-readout-{Guid.NewGuid():N}.json");

        try
        {
            TokenReadoutModelStore.Save(path, TokenReadoutModelStore.CreateArtifact(classifier, preprocessor, parameters, population));
            var loaded = TokenReadoutModelStore.Load(path);

            Assert.Equal(classifier.ClassOrdering, loaded.Classifier.ClassOrdering);
            Assert.Equal(preprocessor.RetainedFeatureIndices, loaded.Preprocessor.RetainedFeatureIndices);
            Assert.Equal(classifier.Predict(preprocessor.Transform(examples[0].Vector)), loaded.Classifier.Predict(loaded.Preprocessor.Transform(examples[0].Vector)));
        }
        finally
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    [Fact]
    public void CoordinateCorpus_IsSeededCappedAndContainsRequiredContexts()
    {
        var corpus = TokenReadoutCoordinateCorpus.Create(30, 42);

        Assert.Equal(30, corpus.Count);
        Assert.Contains(GeoCoordinate.Parse("N5 E7"), corpus);
        Assert.Contains(GeoCoordinate.Parse("N11 E11"), corpus);
        Assert.Contains(GeoCoordinate.Parse("N55 W11"), corpus);
        Assert.Contains(GeoCoordinate.Parse("S33 E111"), corpus);
        Assert.Equal(corpus, TokenReadoutCoordinateCorpus.Create(30, 42));
    }

    private static TokenReadoutExample Example(string coordinateId, GeoCoordinateToken token, params double[] features) =>
        new(coordinateId, token, new NeuralFeatureVector(22, 1, features.Length, 22, features));

    private static PreparedTokenReadoutExample Prepared(string coordinateId, GeoCoordinateToken token, params double[] features) =>
        new(coordinateId, token, features);
}
