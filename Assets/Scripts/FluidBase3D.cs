using System.Collections;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using UnityEngine;

namespace Kodai.Fluid.SPH {


    struct FluidParticleForces3D
    {
        public Vector3 Acceleration;
    };

    public abstract class FluidBase3D<T> : MonoBehaviour where T : struct {

        [SerializeField] protected NumParticleEnum particleNum = NumParticleEnum.NUM_8K;    // パーティクルの個数
        [SerializeField] protected float smoothlen = 0.012f;                                // 粒子半径
        [SerializeField] private float pressureStiffness = 200.0f;                          // 圧力項係数
        [SerializeField] protected float restDensity = 1000.0f;                             // 静止密度
        [SerializeField] protected float particleMass = 0.0002f;                            // 粒子質量
        [SerializeField] protected float viscosity = 0.1f;                                  // 粘性係数
        [SerializeField] protected float maxAllowableTimestep = 0.005f;                     // 時間刻み幅
        [SerializeField] protected float wallStiffness = 3000.0f;                           // ペナルティ法の壁の力
        [SerializeField] protected int iterations = 4;                                      // シミュレーションの1フレーム当たりのイテレーション回数
        [SerializeField] protected Vector3 gravity = new Vector3(0.0f, -0.5f, 0.0f);        // 重力
        [SerializeField] protected Vector3 range = new Vector3(1, 1, 1);                    // シミュレーション空間
        [SerializeField] protected bool simulate = true;                                    // シミュレーション実行 or 一時停止

        private int numParticles;                                                           // パーティクルの個数
        private float timeStep;                                                             // 時間刻み幅
        private float densityCoef;                                                          // Poly6カーネルの密度係数
        private float gradPressureCoef;                                                     // Spikyカーネルの圧力係数
        private float lapViscosityCoef;                                                     // Laplacianカーネルの粘性係数

        #region DirectCompute
        private ComputeShader fluidCS;
        private static readonly int THREAD_SIZE_X = 1024;                                   // コンピュートシェーダ側のスレッド数
        private ComputeBuffer particlesBufferRead;                                          // 粒子のデータを保持するバッファ
        private ComputeBuffer particlesBufferWrite;                                         // 粒子のデータを書き込むバッファ
        private ComputeBuffer particlesPressureBuffer;                                      // 粒子の圧力データを保持するバッファ
        private ComputeBuffer particleDensitiesBuffer;                                      // 粒子の密度データを保持するバッファ
        private ComputeBuffer particleForcesBuffer;                                         // 粒子の加速度データを保持するバッファ
        private ComputeBuffer debugBuffer;                                                  // デバッグ用バッファ
        #endregion

        #region Accessor
        public int NumParticles {
            get { return numParticles; }
        }

        public ComputeBuffer ParticlesBufferRead {
            get { return particlesBufferRead; }
        }
        #endregion

        #region Mono
        protected virtual void Awake() {
            fluidCS = (ComputeShader)Resources.Load("SPH3D");
            numParticles = (int)particleNum;
        } 

        protected virtual void Start() {
            InitBuffers();
        }

        private void Update() {

            if (!simulate) {
                return;
            }

            timeStep = Mathf.Min(maxAllowableTimestep, Time.deltaTime);

            // 2Dカーネル係数
            //densityCoef = particleMass * 4f / (Mathf.PI * Mathf.Pow(smoothlen, 8));                     // Poly6 2D用
            densityCoef = particleMass * 315f / (64f * Mathf.PI * Mathf.Pow(smoothlen, 9));             // Poly6 3D用
            //gradPressureCoef = particleMass * -30.0f / (Mathf.PI * Mathf.Pow(smoothlen, 5));            // Spiky 2D用
            gradPressureCoef = particleMass * -45.0f / (Mathf.PI * Mathf.Pow(smoothlen, 6));            // Spiky 3D用
            //lapViscosityCoef = particleMass * 20f / (3 * Mathf.PI * Mathf.Pow(smoothlen, 5));           // Viscosity 2D用
            lapViscosityCoef = particleMass * 45f / (Mathf.PI * Mathf.Pow(smoothlen, 6));               // Viscosity 3D用

            // シェーダー定数の転送
            fluidCS.SetInt("_NumParticles", numParticles);
            fluidCS.SetFloat("_TimeStep", timeStep);
            fluidCS.SetFloat("_Smoothlen", smoothlen);
            fluidCS.SetFloat("_PressureStiffness", pressureStiffness);
            fluidCS.SetFloat("_RestDensity", restDensity);
            fluidCS.SetFloat("_Viscosity", viscosity);
            fluidCS.SetFloat("_DensityCoef", densityCoef);
            fluidCS.SetFloat("_GradPressureCoef", gradPressureCoef);
            fluidCS.SetFloat("_LapViscosityCoef", lapViscosityCoef);
            fluidCS.SetFloat("_WallStiffness", wallStiffness);
            fluidCS.SetVector("_Range", range);
            fluidCS.SetVector("_Gravity", gravity);

            AdditionalCSParams(fluidCS);

            // 計算精度を上げるために時間刻み幅を小さくして複数回イテレーションする
            for (int i = 0; i<iterations; i++) {
                RunFluidSolver();
            }
        }

        private void OnDestroy() {
            DeleteBuffer(debugBuffer);
            DeleteBuffer(particlesBufferRead);
            DeleteBuffer(particlesBufferWrite);
            DeleteBuffer(particlesPressureBuffer);
            DeleteBuffer(particleDensitiesBuffer);
            DeleteBuffer(particleForcesBuffer);
        }
        #endregion Mono

        /// <summary>
        /// 流体シミュレーションメインルーチン
        /// </summary>
        private void RunFluidSolver() {

            int kernelID = -1;
            int threadGroupsX = numParticles / THREAD_SIZE_X;

            // Density
            kernelID = fluidCS.FindKernel("DensityCS");
            fluidCS.SetBuffer(kernelID, "_ParticlesBufferRead", particlesBufferRead);
            fluidCS.SetBuffer(kernelID, "_ParticlesDensityBufferWrite", particleDensitiesBuffer);
            fluidCS.Dispatch(kernelID, threadGroupsX, 1, 1);

            // Pressure
            kernelID = fluidCS.FindKernel("PressureCS");
            fluidCS.SetBuffer(kernelID, "_ParticlesDensityBufferRead", particleDensitiesBuffer);
            fluidCS.SetBuffer(kernelID, "_ParticlesPressureBufferWrite", particlesPressureBuffer);
            fluidCS.Dispatch(kernelID, threadGroupsX, 1, 1);

            // Force
            kernelID = fluidCS.FindKernel("ForceCS");
            fluidCS.SetBuffer(kernelID, "_ParticlesBufferRead", particlesBufferRead);
            fluidCS.SetBuffer(kernelID, "_ParticlesDensityBufferRead", particleDensitiesBuffer);
            fluidCS.SetBuffer(kernelID, "_ParticlesPressureBufferRead", particlesPressureBuffer);
            fluidCS.SetBuffer(kernelID, "_ParticlesForceBufferWrite", particleForcesBuffer);
            fluidCS.Dispatch(kernelID, threadGroupsX, 1, 1);

            // Integrate
            kernelID = fluidCS.FindKernel("IntegrateCS");
            fluidCS.SetBuffer(kernelID, "_DebugBuffer", debugBuffer);
            fluidCS.SetBuffer(kernelID, "_ParticlesBufferRead", particlesBufferRead);
            fluidCS.SetBuffer(kernelID, "_ParticlesForceBufferRead", particleForcesBuffer);
            fluidCS.SetBuffer(kernelID, "_ParticlesBufferWrite", particlesBufferWrite);
            fluidCS.Dispatch(kernelID, threadGroupsX, 1, 1);

            var result = new float[threadGroupsX];
            debugBuffer.GetData(result);
            foreach (var eachResult in result)
            {
                if (eachResult < 0) Debug.Log(eachResult);
            }

            SwapComputeBuffer(ref particlesBufferRead, ref particlesBufferWrite);   // バッファの入れ替え
        }

        /// <summary>
        /// 子クラスでシェーダー定数の転送を追加する場合はこのメソッドを利用する
        /// </summary>
        /// <param name="shader"></param>
        protected virtual void AdditionalCSParams(ComputeShader shader) { }

        /// <summary>
        /// パーティクル初期位置と初速の設定
        /// </summary>
        /// <param name="particles"></param>
        protected abstract void InitParticleData(ref T[] particles);

        /// <summary>
        /// バッファの初期化
        /// </summary>
        private void InitBuffers() {
            particlesBufferRead = new ComputeBuffer(numParticles, Marshal.SizeOf(typeof(T)));
            var particles = new T[numParticles];
            InitParticleData(ref particles);
            particlesBufferRead.SetData(particles);
            particles = null;

            debugBuffer = new ComputeBuffer(numParticles, sizeof(float));

            particlesBufferWrite = new ComputeBuffer(numParticles, Marshal.SizeOf(typeof(T)));
            particlesPressureBuffer = new ComputeBuffer(numParticles, Marshal.SizeOf(typeof(FluidParticlePressure)));
            particleForcesBuffer = new ComputeBuffer(numParticles, Marshal.SizeOf(typeof(FluidParticleForces3D)));
            particleDensitiesBuffer = new ComputeBuffer(numParticles, Marshal.SizeOf(typeof(FluidParticleDensity)));
        }

        /// <summary>
        /// 引数に指定されたバッファの入れ替え
        /// </summary>
        private void SwapComputeBuffer(ref ComputeBuffer ping, ref ComputeBuffer pong) {
            ComputeBuffer temp = ping;
            ping = pong;
            pong = temp;
        }

        /// <summary>
        /// バッファの開放
        /// </summary>
        /// <param name="buffer"></param>
        private void DeleteBuffer(ComputeBuffer buffer) {
            if (buffer != null) {
                buffer.Release();
                buffer = null;
            }
        }
    }
}