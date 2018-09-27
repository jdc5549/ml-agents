﻿using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
#if UNITY_EDITOR
using UnityEditor;
#endif
using System.Linq;
using Random = UnityEngine.Random;
#if ENABLE_TENSORFLOW
using TensorFlow;

#endif

namespace MLAgents
{
    /// Brain which decides actions using internally embedded TensorFlow model.
    [CreateAssetMenu(fileName = "NewLearningBrain", menuName = "ML-Agents/Learning Brain")]
    public class LearningBrain : Brain
    {
        [System.Serializable]
        private struct TensorFlowAgentPlaceholder
        {
            public enum TensorType
            {
                Integer,
                FloatingPoint
            };

            public string name;
            public TensorType valueType;
            public float minValue;
            public float maxValue;
        }

        [Tooltip("This must be the bytes file corresponding to the pretrained TensorFlow graph.")]
        /// Modify only in inspector : Reference to the Graph asset
        public TextAsset graphModel;

        [NonSerialized]
        private bool isControlled;

        [SerializeField]
        [Tooltip(
            "If your graph takes additional inputs that are fixed (example: noise level) you can specify them here.")]
        ///  Modify only in inspector : If your graph takes additional inputs that are fixed you can specify them here.
        private TensorFlowAgentPlaceholder[] graphPlaceholders;

        ///  Modify only in inspector : Name of the placholder of the batch size
        public string BatchSizePlaceholderName = "batch_size";

        ///  Modify only in inspector : Name of the state placeholder
        public string VectorObservationPlacholderName = "vector_observation";

        ///  Modify only in inspector : Name of the recurrent input
        public string RecurrentInPlaceholderName = "recurrent_in";

        ///  Modify only in inspector : Name of the recurrent output
        public string RecurrentOutPlaceholderName = "recurrent_out";

        /// Modify only in inspector : Names of the observations placeholders
        public string[] VisualObservationPlaceholderName;

        /// Modify only in inspector : Name of the action node
        public string ActionPlaceholderName = "action";

        /// Modify only in inspector : Name of the previous action node
        public string PreviousActionPlaceholderName = "prev_action";

        /// Name of the action mask node
        private string ActionMaskPlaceholderName = "action_masks";
        
#if ENABLE_TENSORFLOW
        TFGraph graph;
        TFSession session;
        bool hasRecurrent;
        bool hasState;
        bool hasBatchSize;
        bool hasPrevAction;
        bool hasMaskedActions; 
        bool hasValueEstimate;
        float[,] inputState;
        int[,] inputPrevAction;
        List<float[,,,]> observationMatrixList;
        float[,] inputOldMemories;
        float[,] maskedActions;
        List<Texture2D> texturesHolder;
        int memorySize;
#endif

        /// <summary>
        /// When Called, the brain will be controlled externally. It will not use the
        /// model to decide on actions.
        /// </summary>
        public void SetToControlledExternally()
        {
            isControlled = true;
        }
        
        protected override void Initialize()
        {
#if ENABLE_TENSORFLOW
#if UNITY_ANDROID && !UNITY_EDITOR
// This needs to ba called only once and will raise an exception if 
// there are multiple internal brains
        try{
            TensorFlowSharp.Android.NativeBinding.Init();
        }
        catch{
            
        }
#endif

            if (graphModel != null)
            {
                graph = new TFGraph();

                graph.Import(graphModel.bytes);

                session = new TFSession(graph);

                if (graph[BatchSizePlaceholderName] != null)
                {
                    hasBatchSize = true;
                }

                if ((graph[RecurrentInPlaceholderName] != null) &&
                    (graph[RecurrentOutPlaceholderName] != null))
                {
                    hasRecurrent = true;
                    var runner = session.GetRunner();
                    runner.Fetch(graph["memory_size"][0]);
                    var networkOutput = runner.Run()[0].GetValue();
                    memorySize = (int) networkOutput;
                }

                if (graph[VectorObservationPlacholderName] != null)
                {
                    hasState = true;
                }

                if (graph[PreviousActionPlaceholderName] != null)
                {
                    hasPrevAction = true;
                }
                if (graph["value_estimate"] != null)
                {
                    hasValueEstimate = true;
                }
                if (graph[ActionMaskPlaceholderName] != null)
                {
                    hasMaskedActions = true;
                }
            }


            observationMatrixList = new List<float[,,,]>();
            texturesHolder = new List<Texture2D>();
#endif
        }


        /// Uses the stored information to run the tensorflow graph and generate 
        /// the actions.
        protected override void DecideAction()
        {
#if ENABLE_TENSORFLOW
            base.DecideAction();
            
            if (isControlled)
            {
                agentInfos.Clear();
                return;
            }

            int currentBatchSize = agentInfos.Count();
            List<Agent> agentList = agentInfos.Keys.ToList();
            if (currentBatchSize == 0)
            {
                return;
            }
            

            // Create the state tensor
            if (hasState)
            {
                int stateLength = 1;
                stateLength = brainParameters.vectorObservationSize;
                inputState =
                    new float[currentBatchSize, stateLength * brainParameters.numStackedVectorObservations];

                var i = 0;
                foreach (Agent agent in agentList)
                {
                    List<float> stateList = agentInfos[agent].stackedVectorObservation;
                    for (int j =
                            0;
                        j < stateLength * brainParameters.numStackedVectorObservations;
                        j++)
                    {
                        inputState[i, j] = stateList[j];
                    }

                    i++;
                }
            }

            // Create the state tensor
            if (hasPrevAction)
            {
                int totalNumberActions = brainParameters.vectorActionSize.Length;
                inputPrevAction = new int[currentBatchSize, totalNumberActions];
                var i = 0;
                foreach (Agent agent in agentList)
                {
                    float[] actionList = agentInfos[agent].storedVectorActions;
                    for (var j = 0 ; j < totalNumberActions; j++)
                    {
                        inputPrevAction[i,j] = Mathf.FloorToInt(actionList[j]);
                    }
                    i++;
                }
            }
            
            if (hasMaskedActions)
            {
                maskedActions = new float[
                    currentBatchSize, 
                    brainParameters.vectorActionSize.Sum()
                ];
                var i = 0;
                foreach (Agent agent in agentList)
                {
                    for (int j = 0; j < brainParameters.vectorActionSize.Sum(); j++)
                    {
                        if (agentInfos[agent].actionMasks != null)
                        {
                            maskedActions[i, j] = agentInfos[agent].actionMasks[j] ? 0.0f : 1.0f;
                        }
                        else
                        {
                            maskedActions[i, j] = 1.0f;
                        }
                    }
                    i++;
                }
            }
            
            observationMatrixList.Clear();
            for (int observationIndex =
                    0;
                observationIndex < brainParameters.cameraResolutions.Length;
                observationIndex++)
            {
                texturesHolder.Clear();
                foreach (Agent agent in agentList)
                {
                    texturesHolder.Add(agentInfos[agent].visualObservations[observationIndex]);
                }

                observationMatrixList.Add(
                    BatchVisualObservations(texturesHolder,
                        brainParameters.cameraResolutions[observationIndex].blackAndWhite));
            }

            // Create the recurrent tensor
            if (hasRecurrent)
            {
                // Need to have variable memory size
                inputOldMemories = new float[currentBatchSize, memorySize];
                var i = 0;
                foreach (Agent agent in agentList)
                {
                    float[] m = agentInfos[agent].memories.ToArray();
                    for (int j = 0; j < m.Length; j++)
                    {
                        inputOldMemories[i, j] = m[j];
                    }

                    i++;
                }
            }


            var runner = session.GetRunner();
            try
            {
                runner.Fetch(graph[ActionPlaceholderName][0]);
            }
            catch
            {
                throw new UnityAgentsException(string.Format(
                    @"The node {0} could not be found. Please make sure the node name is correct",
                    ActionPlaceholderName));
            }

            if (hasBatchSize)
            {
                runner.AddInput(graph[BatchSizePlaceholderName][0], new int[] {currentBatchSize});
            }

            foreach (TensorFlowAgentPlaceholder placeholder in graphPlaceholders)
            {
                try
                {
                    if (placeholder.valueType == TensorFlowAgentPlaceholder.TensorType.FloatingPoint)
                    {
                        runner.AddInput(graph[placeholder.name][0],
                            new float[] {Random.Range(placeholder.minValue, placeholder.maxValue)});
                    }
                    else if (placeholder.valueType == TensorFlowAgentPlaceholder.TensorType.Integer)
                    {
                        runner.AddInput(graph[placeholder.name][0],
                            new int[] {Random.Range((int) placeholder.minValue, (int) placeholder.maxValue + 1)});
                    }
                }
                catch
                {
                    throw new UnityAgentsException(string.Format(
                        @"One of the Tensorflow placeholder cound nout be found.
                In brain {0}, there are no {1} placeholder named {2}.",
                        name, placeholder.valueType.ToString(), placeholder.name));
                }
            }

            // Create the state tensor
            if (hasState)
            {
                runner.AddInput(graph[VectorObservationPlacholderName][0], inputState);
            }

            // Create the previous action tensor
            if (hasPrevAction)
            {
                runner.AddInput(graph[PreviousActionPlaceholderName][0], inputPrevAction);
            }

            // Create the mask action tensor
            if (hasMaskedActions)
            {
                runner.AddInput(graph[ActionMaskPlaceholderName][0], maskedActions);
            }
            
            // Create the observation tensors
            for (int obsNumber =
                    0;
                obsNumber < brainParameters.cameraResolutions.Length;
                obsNumber++)
            {
                runner.AddInput(graph[VisualObservationPlaceholderName[obsNumber]][0],
                    observationMatrixList[obsNumber]);
            }

            if (hasRecurrent)
            {
                runner.AddInput(graph["sequence_length"][0], 1);
                runner.AddInput(graph[RecurrentInPlaceholderName][0], inputOldMemories);
                runner.Fetch(graph[RecurrentOutPlaceholderName][0]);
            }

            if (hasValueEstimate)
            {
                runner.Fetch(graph["value_estimate"][0]);
            }

            TFTensor[] networkOutput;
            try
            {
                networkOutput = runner.Run();
            }
            catch (TFException e)
            {
                string errorMessage = e.Message;
                try
                {
                    errorMessage =
                        $@"The tensorflow graph needs an input for {e.Message.Split(new string[] {"Node: "}, 0)[1].Split('=')[0]} of type {e.Message.Split(new string[] {"dtype="}, 0)[1].Split(',')[0]}";
                }
                finally
                {
                    throw new UnityAgentsException(errorMessage);
                }
            }

            // Create the recurrent tensor
            if (hasRecurrent)
            {
                float[,] recurrentTensor = networkOutput[1].GetValue() as float[,];

                var i = 0;
                foreach (Agent agent in agentList)
                {
                    var m = new float[memorySize];
                    for (int j = 0; j < memorySize; j++)
                    {
                        m[j] = recurrentTensor[i, j];
                    }

                    agent.UpdateMemoriesAction(m.ToList());
                    i++;
                }
            }

            
            if (hasValueEstimate)
            {
                float[,] value_estimates = new float[currentBatchSize,1];
                if (hasRecurrent)
                {
                    value_estimates = networkOutput[2].GetValue() as float[,];
                }
                else
                {
                    value_estimates = networkOutput[1].GetValue() as float[,];
                }
                
                var i = 0;
                foreach (Agent agent in agentList)
                {
                    agent.UpdateValueAction(value_estimates[i,0]);
                }
            }

            if (brainParameters.vectorActionSpaceType == SpaceType.continuous)
            {
                var output = networkOutput[0].GetValue() as float[,];
                var i = 0;
                foreach (Agent agent in agentList)
                {
                    var a = new float[brainParameters.vectorActionSize[0]];
                    for (int j = 0; j < brainParameters.vectorActionSize[0]; j++)
                    {
                        a[j] = output[i, j];
                    }

                    agent.UpdateVectorAction(a);
                    i++;
                }
            }
            else if (brainParameters.vectorActionSpaceType == SpaceType.discrete)
            {
                long[,] output = networkOutput[0].GetValue() as long[,];
                var i = 0;
                foreach (Agent agent in agentList)
                {
                    var actSize = brainParameters.vectorActionSize.Length;
                    var a = new float[actSize];
                    for (int actIdx = 0; actIdx < actSize; actIdx++)
                    {
                        a[actIdx] = output[i, actIdx];
                    }
                    agent.UpdateVectorAction(a);
                    i++;
                }
            }


#else
            if (agentInfo.Count > 0)
            {
                throw new UnityAgentsException(string.Format(
                    @"The brain {0} was set to Internal but the Tensorflow 
                        library is not present in the Unity project.",
                    brain.gameObject.name));
            }
#endif
            agentInfos.Clear();
        }

        /// <summary>
        /// Converts a list of Texture2D into a Tensor.
        /// </summary>
        /// <returns>
        /// A 4 dimensional float Tensor of dimension
        /// [batch_size, height, width, channel].
        /// Where batch_size is the number of input textures,
        /// height corresponds to the height of the texture,
        /// width corresponds to the width of the texture,
        /// channel corresponds to the number of channels extracted from the
        /// input textures (based on the input blackAndWhite flag
        /// (3 if the flag is false, 1 otherwise).
        /// The values of the Tensor are between 0 and 1.
        /// </returns>
        /// <param name="textures">
        /// The list of textures to be put into the tensor.
        /// Note that the textures must have same width and height.
        /// </param>
        /// <param name="blackAndWhite">
        /// If set to <c>true</c> the textures
        /// will be converted to grayscale before being stored in the tensor.
        /// </param>
        public static float[,,,] BatchVisualObservations(
            List<Texture2D> textures, bool blackAndWhite)
        {
            int batchSize = textures.Count();
            int width = textures[0].width;
            int height = textures[0].height;
            int pixels = 0;
            if (blackAndWhite)
                pixels = 1;
            else
                pixels = 3;
            float[,,,] result = new float[batchSize, height, width, pixels];
            float[] resultTemp = new float[batchSize * height * width * pixels];
            int hwp = height * width * pixels;
            int wp = width * pixels;

            for (int b = 0; b < batchSize; b++)
            {
                Color32[] cc = textures[b].GetPixels32();
                for (int h = height - 1; h >= 0; h--)
                {
                    for (int w = 0; w < width; w++)
                    {
                        Color32 currentPixel = cc[(height - h - 1) * width + w];
                        if (!blackAndWhite)
                        {
                            // For Color32, the r, g and b values are between
                            // 0 and 255.
                            resultTemp[b * hwp + h * wp + w * pixels] = currentPixel.r / 255.0f;
                            resultTemp[b * hwp + h * wp + w * pixels + 1] = currentPixel.g / 255.0f;
                            resultTemp[b * hwp + h * wp + w * pixels + 2] = currentPixel.b / 255.0f;
                        }
                        else
                        {
                            resultTemp[b * hwp + h * wp + w * pixels] =
                                (currentPixel.r + currentPixel.g + currentPixel.b)
                                / 3f / 255.0f;
                        }
                    }
                }
            }

            System.Buffer.BlockCopy(resultTemp, 0, result, 0, batchSize * hwp * sizeof(float));
            return result;
        }
    }
}